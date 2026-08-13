/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// <para>
/// Debugger control + state via the PUBLIC <see cref="Debugger"/> API (no reflection
/// needed, unlike the Roslyn-internal navigation tools). This first slice covers session
/// control (start/stop), breakpoints, and reading the debug state — none of which need the
/// debuggee to be paused. Live inspection (locals/call-stack/exception) comes later and only
/// works in break mode.
/// </para>
/// <para>
/// All access is on the VS UI thread (EnvDTE is STA/main-thread bound). Never throws: each
/// call returns a structured result so the MCP tool can report failure to the model.
/// </para>
/// </summary>
internal sealed partial class IdeDebugService
{
    public static IdeDebugService Instance { get; } = new();



    /// <summary>Read the current debug state (mode, and in break mode the active location).</summary>
    public async Task<DebugState> GetStateAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugState { Mode = "unknown" }; }

            var state = new DebugState { Mode = ModeToString(dbg.CurrentMode) };
            if (dbg.CurrentMode == dbgDebugMode.dbgBreakMode)
            {
                // In break mode VS moves the caret to the current statement; the active
                // document's selection gives us file + line of where execution is paused.
                // (EnvDTE's StackFrame exposes only FunctionName, not a source location.)
                try
                {
                    var doc = GetDte()?.ActiveDocument;
                    state.CurrentFile = doc?.FullName;
                    if (doc?.Selection is TextSelection sel) { state.CurrentLine = sel.ActivePoint.Line; }
                }
                catch { /* location not available — leave file/line unset */ }

                // If we're paused on an exception, $exception holds it (debugger pseudo-variable).
                // Absent/invalid when the break is a plain breakpoint — leave the fields null.
                try
                {
                    var exObj = dbg.GetExpression("$exception", false, 200);
                    if (exObj?.IsValidValue == true && !string.IsNullOrEmpty(exObj.Type))
                    {
                        state.ExceptionType = exObj.Type;
                        var msg = dbg.GetExpression("$exception.Message", false, 200);
                        if (msg?.IsValidValue == true) { state.ExceptionMessage = Unquote(msg.Value); }
                    }
                }
                catch { /* not an exception break, or engine doesn't expose $exception */ }
            }
            return state;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetStateAsync", ex);
            return new DebugState { Mode = "unknown" };
        }
    }

    /// <summary>Start debugging the startup project(s), like F5. Non-blocking: returns once the
    /// session has been launched (the program then runs until it hits a breakpoint/ends).</summary>
    public async Task<DebugResult> StartAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgDesignMode)
            {
                // Already running or paused — don't relaunch.
                return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = "Already debugging." };
            }
            dbg.Go(false); // false = don't block waiting for break/end
            // No Mode: Go() returns before the transition, so CurrentMode here is still the state we
            // just left — reporting it said "design" for a session that had started. getDebugState
            // is the one that knows, and the caller has to poll it anyway.
            return new DebugResult { Ok = true, Mode = PendingMode, Reason = "Debugging started — poll debug_get_state for the mode." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.StartAsync", ex);
            // VS refuses to start while it is still building or saving, and answers with a message
            // that says which — swallowing it left the caller with "Failed to start debugging" and
            // no idea that trying again a second later would work. The mode goes back too: on
            // failure it is the real one, not the "pending" a start that took would report.
            return new DebugResult
            {
                Ok = false,
                Mode = ModeToString(GetDebugger()?.CurrentMode ?? dbgDebugMode.dbgDesignMode),
                Reason = $"Could not start debugging: {ex.Message} " +
                         "Visual Studio refuses this while a build or a save is still in flight — retrying shortly usually works.",
            };
        }
    }

    /// <summary>Stop the current debug session (like Shift+F5). No-op if not debugging.</summary>
    public async Task<DebugResult> StopAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                return new DebugResult { Ok = true, Mode = "design", Reason = "Not debugging." };
            }
            dbg.Stop(false);
            // Same as Go(): non-blocking, so CurrentMode has not caught up yet.
            return new DebugResult { Ok = true, Mode = PendingMode, Reason = "Stop requested — poll debug_get_state to see it land." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.StopAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to stop debugging." };
        }
    }

    /// <summary>Move the instruction pointer to <paramref name="filePath"/>:<paramref name="line"/>
    /// WITHOUT running what lies between — "Set Next Statement".
    /// <para>There is no API taking a path: the command works off the caret, so the file is opened,
    /// activated and the caret moved first. Stays in break mode, so nothing has to be awaited
    /// afterwards.</para>
    /// <para>The one debug tool that can leave the program inconsistent — skipping an assignment
    /// leaves the variable at whatever it was, and jumping backwards re-runs side effects.</para>
    /// <para>The file is checked against where execution is paused, because the command does NOT:
    /// it takes a caret position and resolves the line as an offset in the method it is already in.
    /// Asked to jump into another file it moves the pointer to that line NUMBER in the current
    /// method — a different statement entirely, and a stack whose frame and instruction pointer
    /// disagree. Verified: VS accepts it without a word.</para></summary>
    public async Task<DebugResult> SetNextStatementAsync(string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = GetDte();
            var dbg = dte?.Debugger;
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrEmpty(filePath) || line < 1)
            {
                return new DebugResult { Ok = false, Reason = "filePath and a 1-based line are required." };
            }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new DebugResult { Ok = false, Reason = NotInBreak };
            }

            var (pausedFile, _) = CurrentLocation();
            if (!string.IsNullOrEmpty(pausedFile)
                && !string.Equals(System.IO.Path.GetFullPath(filePath), System.IO.Path.GetFullPath(pausedFile), StringComparison.OrdinalIgnoreCase))
            {
                return new DebugResult
                {
                    Ok = false,
                    Reason = $"The jump must stay in the file execution is paused in ({System.IO.Path.GetFileName(pausedFile)}); "
                             + $"{System.IO.Path.GetFileName(filePath)} is a different one. Set Next Statement cannot leave the current method.",
                };
            }

            var window = dte.ItemOperations.OpenFile(filePath, Constants.vsViewKindCode);
            window?.Activate();
            if (window?.Document?.Selection is not TextSelection sel)
            {
                return new DebugResult { Ok = false, Reason = $"Could not open {System.IO.Path.GetFileName(filePath)} in the editor." };
            }
            sel.MoveToLineAndOffset(line, 1, false);

            // Throws when VS judges the jump impossible — out of the current frame, into or out of
            // a handler. The message is the debugger's own, and more specific than anything we
            // could say about why this particular jump was refused.
            try { dte.ExecuteCommand("Debug.SetNextStatement", ""); }
            catch (Exception ex) { return new DebugResult { Ok = false, Reason = $"Visual Studio refused the jump: {ex.Message.Trim()}" }; }

            // The line asked for, not CurrentLocation(): we are still stopped on whatever breakpoint
            // got us here, so that would report where the jump came FROM. It is also the one place
            // that already knows the answer — the jump either landed on the requested line or threw.
            return new DebugResult
            {
                Ok = true,
                Mode = ModeToString(dbg.CurrentMode),
                Reason = $"Next statement is now {System.IO.Path.GetFileName(filePath)}:{line}. The skipped code did not run.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.SetNextStatementAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set the next statement." };
        }
    }

    /// <summary>Run until execution reaches <paramref name="filePath"/>:<paramref name="line"/>,
    /// like "Run to Cursor".
    /// <para>Through VS's own command, off the caret, the way SetNextStatement works. The obvious
    /// build — add a breakpoint, Go(), delete it — does not: Go() is non-blocking, so the delete
    /// runs while the program is still on its way and disarms the breakpoint before it is ever hit.
    /// Measured: execution sailed past the line, twice out of twice.</para>
    /// <para>Non-blocking like Go(): poll debug_get_state to see where it stopped, which may not be
    /// the requested line — anything on the way there pauses it first.</para></summary>
    public async Task<DebugResult> RunToLineAsync(string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = GetDte();
            var dbg = dte?.Debugger;
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrEmpty(filePath) || line < 1)
            {
                return new DebugResult { Ok = false, Reason = "filePath and a 1-based line are required." };
            }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new DebugResult { Ok = false, Reason = NotInBreak };
            }

            var window = dte.ItemOperations.OpenFile(filePath, Constants.vsViewKindCode);
            window?.Activate();
            if (window?.Document?.Selection is not TextSelection sel)
            {
                return new DebugResult { Ok = false, Reason = $"Could not open {System.IO.Path.GetFileName(filePath)} in the editor." };
            }
            sel.MoveToLineAndOffset(line, 1, false);

            try { dte.ExecuteCommand("Debug.RunToCursor", ""); }
            catch (Exception ex) { return new DebugResult { Ok = false, Reason = $"Visual Studio refused it: {ex.Message.Trim()}" }; }

            return new DebugResult { Ok = true, Reason = $"Running to {System.IO.Path.GetFileName(filePath)}:{line} — poll debug_get_state; it may stop earlier." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.RunToLineAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to run to that line." };
        }
    }

    /// <summary>How many times a breakpoint has actually been hit.
    ///
    /// The breakpoint in <c>Debugger.Breakpoints</c> is the one that was *asked for*; when the
    /// debugger binds it, the hits land on the bound children it creates — one per address the
    /// location resolved to. Reading the parent's own CurrentHits therefore answers 0 for a
    /// breakpoint that has been hit five hundred times, which is worse than not reporting it at
    /// all. Must be called on the UI thread.</summary>
    private static int CountHits(Breakpoint bp)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (bp is not EnvDTE80.Breakpoint2 bp2) { return 0; }
        try
        {
            var own = bp2.CurrentHits;
            if (bp2.Children == null || bp2.Children.Count == 0) { return own; }

            var total = 0;
            foreach (Breakpoint child in bp2.Children)
            {
                if (child is EnvDTE80.Breakpoint2 c) { total += c.CurrentHits; }
            }
            // The parent counts too on some bindings; take whichever knows more rather than
            // double-counting an unbound one.
            return Math.Max(own, total);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[debug] could not read the hit count of a breakpoint: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The reverse of <see cref="HitCountRule"/>, for reporting a breakpoint back.</summary>
    private static string HitCountTypeToString(dbgHitCountType type) => type switch
    {
        dbgHitCountType.dbgHitCountTypeEqual => "equal",
        dbgHitCountType.dbgHitCountTypeGreaterOrEqual => "greaterOrEqual",
        dbgHitCountType.dbgHitCountTypeMultiple => "multiple",
        _ => null,
    };

    /// <summary>Translate a hit-count rule into the EnvDTE pair. A count of 0 means "break every
    /// time", which is <c>None</c> — the type is ignored then, so an unknown name is only reported
    /// when a count was actually given.</summary>
    private static (dbgHitCountType Type, string Error) HitCountRule(int hitCount, string hitCountType)
    {
        if (hitCount <= 0) { return (dbgHitCountType.dbgHitCountTypeNone, null); }
        return (hitCountType ?? "equal") switch
        {
            "equal" => (dbgHitCountType.dbgHitCountTypeEqual, null),
            "greaterOrEqual" => (dbgHitCountType.dbgHitCountTypeGreaterOrEqual, null),
            "multiple" => (dbgHitCountType.dbgHitCountTypeMultiple, null),
            _ => (dbgHitCountType.dbgHitCountTypeNone,
                  $"Unknown hitCountType '{hitCountType}'. Use 'equal', 'greaterOrEqual' or 'multiple'."),
        };
    }

    /// <summary>Add a breakpoint at <paramref name="filePath"/>:<paramref name="line"/>, with an
    /// optional condition (true-expression) and hit-count rule. Works in any mode. Returns Ok even
    /// if VS can't bind it yet (it binds when the code loads), as long as the request was
    /// accepted.</summary>
    public async Task<DebugResult> SetBreakpointAsync(string filePath, int line, string condition,
                                                      int hitCount = 0, string hitCountType = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrEmpty(filePath) || line < 1)
            {
                return new DebugResult { Ok = false, Reason = "filePath and a 1-based line are required." };
            }

            // Breakpoints.Add(Function, File, Line, Column, Condition, ConditionType, Language,
            //   Data, DataCount, Address, HitCount, HitCountType). File+Line is the file breakpoint.
            // DataCount default is 1 per the API (0 is invalid). Language is left empty: VS binds from
            // the project/symbols at bind time (works for a closed file too, and for any language),
            // so an extension→name guess added no value and only covered a handful of languages.
            var (hitType, hitError) = HitCountRule(hitCount, hitCountType);
            if (hitError != null) { return new DebugResult { Ok = false, Reason = hitError }; }

            var hasCond = !string.IsNullOrWhiteSpace(condition);
            dbg.Breakpoints.Add(
                Function: "",
                File: filePath,
                Line: line,
                Column: 1,
                Condition: hasCond ? condition : "",
                ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                Language: "",
                Data: "",
                DataCount: 1,
                Address: "",
                HitCount: Math.Max(0, hitCount),
                HitCountType: hitType);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.SetBreakpointAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set breakpoint." };
        }
    }

    /// <summary>Add a breakpoint that triggers on entry to a function by NAME (e.g.
    /// "MyNamespace.MyClass.Calculate"), instead of a file/line — handy when you know the method
    /// but not the line. Optional condition. Works in any mode.</summary>
    public async Task<DebugResult> SetFunctionBreakpointAsync(string functionName, string condition,
                                                              int hitCount = 0, string hitCountType = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrWhiteSpace(functionName))
            {
                return new DebugResult { Ok = false, Reason = "functionName is required." };
            }

            var (hitType, hitError) = HitCountRule(hitCount, hitCountType);
            if (hitError != null) { return new DebugResult { Ok = false, Reason = hitError }; }

            var hasCond = !string.IsNullOrWhiteSpace(condition);
            dbg.Breakpoints.Add(
                Function: functionName,
                File: "",
                Line: 1,
                Column: 1,
                Condition: hasCond ? condition : "",
                ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                Language: "", // a bare function name has no file → no language to infer
                Data: "",
                DataCount: 1, // API default; 0 is invalid
                Address: "",
                HitCount: Math.Max(0, hitCount),
                HitCountType: hitType);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.SetFunctionBreakpointAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set function breakpoint." };
        }
    }

    /// <summary>Remove the breakpoint(s) at <paramref name="filePath"/>:<paramref name="line"/>.
    /// Returns the number removed in Reason (0 if none matched).</summary>
    public async Task<DebugResult> RemoveBreakpointAsync(string filePath, int line)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrEmpty(filePath) || line < 1)
            {
                return new DebugResult { Ok = false, Reason = "filePath and a 1-based line are required." };
            }

            int removed = 0;
            // Iterate a snapshot: deleting mutates the collection. Breakpoints.Item is 1-based.
            var toDelete = new List<Breakpoint>();
            foreach (Breakpoint bp in dbg.Breakpoints)
            {
                if (bp.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile
                    && bp.FileLine == line
                    && string.Equals(bp.File, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    toDelete.Add(bp);
                }
            }
            foreach (var bp in toDelete) { bp.Delete(); removed++; }

            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = $"Removed {removed}." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.RemoveBreakpointAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to remove breakpoint." };
        }
    }

    /// <summary>Remove ALL breakpoints in the solution.</summary>
    public async Task<DebugResult> ClearBreakpointsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }

            int removed = 0;
            var toDelete = new List<Breakpoint>();
            foreach (Breakpoint bp in dbg.Breakpoints) { toDelete.Add(bp); }
            foreach (var bp in toDelete) { bp.Delete(); removed++; }

            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = $"Removed {removed}." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ClearBreakpointsAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to clear breakpoints." };
        }
    }

    /// <summary>List all breakpoints in the solution (file/line or function, condition, enabled).</summary>
    public async Task<BreakpointsResult> ListBreakpointsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new BreakpointsResult { Ok = false, Reason = "Debugger not available." }; }

            var list = new List<BreakpointInfo>();
            foreach (Breakpoint bp in dbg.Breakpoints)
            {
                var isFile = bp.LocationType == dbgBreakpointLocationType.dbgBreakpointLocationTypeFile;
                list.Add(new BreakpointInfo
                {
                    File = isFile ? bp.File : null,
                    Line = isFile ? bp.FileLine : 0,
                    Function = isFile ? null : bp.FunctionName,
                    Condition = string.IsNullOrEmpty(bp.Condition) ? null : bp.Condition,
                    Enabled = bp.Enabled,
                    // HitCountTarget is only meaningful with a type set: a breakpoint that was never
                    // given a rule still reports a target of 1, which read as "break on the first
                    // hit" when nobody had asked for one.
                    HitCount = bp.HitCountType == dbgHitCountType.dbgHitCountTypeNone ? 0 : bp.HitCountTarget,
                    HitCountType = HitCountTypeToString(bp.HitCountType),
                    CurrentHits = CountHits(bp),
                });
            }
            // Stable order so the model can compare across calls (file bps by file/line,
            // function bps by name).
            var ordered = list
                .OrderBy(b => b.File ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Line)
                .ThenBy(b => b.Function ?? "", StringComparer.Ordinal)
                .ToArray();
            return new BreakpointsResult { Ok = true, Breakpoints = ordered };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ListBreakpointsAsync", ex);
            return new BreakpointsResult { Ok = false, Reason = "Failed to list breakpoints." };
        }
    }

    /// <summary>Pause the running app NOW (like Debug > Break All), without waiting for a
    /// breakpoint. Only valid while running.</summary>
    public async Task<DebugResult> BreakAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgRunMode)
            {
                return new DebugResult { Ok = false, Mode = ModeToString(dbg.CurrentMode), Reason = "Can only break while running." };
            }
            dbg.Break(false); // false = don't block until the break completes
            // No Mode, same as start/stop/restart: the call returns before the transition, so
            // CurrentMode here still says "run" for a program that is about to be paused.
            return new DebugResult { Ok = true, Reason = "Break requested — poll debug_get_state for where it stopped." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.BreakAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to break." };
        }
    }

    /// <summary>List local processes the debugger can attach to (pid + name), optionally filtered
    /// by a name substring. Sorted by name then pid.</summary>
    public async Task<ProcessesResult> ListProcessesAsync(string nameFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new ProcessesResult { Ok = false, Reason = "Debugger not available." }; }

            var list = new List<ProcessInfo>();
            foreach (Process proc in dbg.LocalProcesses)
            {
                var name = proc.Name ?? "";
                if (!string.IsNullOrEmpty(nameFilter)
                    && name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                list.Add(new ProcessInfo { Pid = proc.ProcessID, Name = name });
            }
            var ordered = list
                .OrderBy(p => System.IO.Path.GetFileName(p.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Pid)
                .ToArray();
            return new ProcessesResult { Ok = true, Processes = ordered };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ListProcessesAsync", ex);
            return new ProcessesResult { Ok = false, Reason = "Failed to list processes." };
        }
    }

    /// <summary>Attach the debugger to a local process by PID (preferred) or by name substring.
    /// This is the primary AI debugging workflow: the app is already running, attach and inspect.
    /// After attaching, the session is in Running mode (use debugBreak or a breakpoint to pause).</summary>
    public async Task<DebugResult> AttachAsync(int pid, string processName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (pid <= 0 && string.IsNullOrWhiteSpace(processName))
            {
                return new DebugResult { Ok = false, Reason = "Provide a pid or a processName." };
            }

            // Match by PID first (unambiguous); otherwise by name substring (must be unique).
            Process match = null;
            int nameMatches = 0;
            foreach (Process proc in dbg.LocalProcesses)
            {
                if (pid > 0)
                {
                    if (proc.ProcessID == pid) { match = proc; break; }
                }
                else if ((proc.Name ?? "").IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    match = proc;
                    nameMatches++;
                }
            }

            if (match == null)
            {
                return new DebugResult { Ok = false, Reason = pid > 0 ? $"No process with pid {pid}." : $"No process matching '{processName}'." };
            }
            if (pid <= 0 && nameMatches > 1)
            {
                return new DebugResult { Ok = false, Reason = $"'{processName}' matches {nameMatches} processes — pass a pid to disambiguate (use listProcesses)." };
            }

            match.Attach();
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.AttachAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to attach (admin rights may be required)." };
        }
    }

    /// <summary>Restart the current debug session (stop, then start again). Convenience over
    /// stopDebug + startDebug.</summary>
    public async Task<DebugResult> RestartAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = GetDte();
            var dbg = dte?.Debugger;
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                // Nothing to restart — just start.
                dbg.Go(false);
                return new DebugResult { Ok = true, Reason = "Was not debugging; started — poll debug_get_state for the mode." };
            }
            // Debug.Restart command handles stop+start cleanly across project types. Like Go()/Stop()
            // it returns before the mode changes, so there is no state worth reporting here.
            dte.ExecuteCommand("Debug.Restart", "");
            return new DebugResult { Ok = true, Reason = "Restart requested — poll debug_get_state for the mode." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.RestartAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to restart debugging." };
        }
    }

    /// <summary>Start the program without the debugger (Ctrl+F5). If projectName is given, sets
    /// it as the startup project first via IdeContextService.</summary>
    /// <summary>Detach the debugger and leave the program running — Debug ▸ Detach All. Different
    /// from <see cref="StopAsync"/>, which kills the process: after attaching to something the
    /// user did not launch, killing it is rarely what was meant.</summary>
    public async Task<DebugResult> DetachAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                return new DebugResult { Ok = true, Mode = "design", Reason = "Not debugging." };
            }

            // Collected before detaching: once it is off the debugger's list, the pid is gone with
            // it — and the pid is the whole point of the message, since the process outlives the
            // session and whoever wants it gone has to find it by hand otherwise.
            var pids = new List<int>();
            foreach (EnvDTE.Process p in dbg.DebuggedProcesses)
            {
                // Process2.Detach takes WaitForBreakOrEnd; false returns as soon as the request is
                // in, like every other transition here.
                if (p is EnvDTE80.Process2 p2) { pids.Add(p2.ProcessID); p2.Detach(false); }
            }

            return pids.Count == 0
                ? new DebugResult { Ok = false, Reason = "No debugged process could be detached." }
                : new DebugResult
                {
                    Ok = true,
                    Mode = PendingMode,
                    Reason = $"Detached from PID {string.Join(", ", pids)} — still running, and no longer under the " +
                             "debugger, so stopping now means killing the process. " +
                             "Poll debug_get_state for the mode — the transition is not immediate.",
                };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.DetachAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to detach." };
        }
    }

    /// <summary>Which exceptions are set to break, per group. Reports only what was changed from
    /// the group default — the full list runs to thousands of types and says nothing.</summary>
    public async Task<(bool Ok, List<ExceptionBreakSetting> Settings, string Reason)> GetExceptionSettingsAsync(string group)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            var groups = (dbg as EnvDTE90.Debugger3)?.ExceptionGroups;
            if (groups == null)
            {
                return (false, null, "Exception settings not available on this debugger.");
            }

            var wanted = string.IsNullOrWhiteSpace(group) ? null : group;
            var list = new List<ExceptionBreakSetting>();
            foreach (EnvDTE90.ExceptionSettings gs in groups)
            {
                if (wanted != null && !string.Equals(gs.Name, wanted, StringComparison.OrdinalIgnoreCase)) { continue; }
                foreach (EnvDTE90.ExceptionSetting s in gs)
                {
                    try
                    {
                        if (!s.BreakWhenThrown) { continue; }
                        list.Add(new ExceptionBreakSetting { Group = gs.Name, Name = s.Name, BreakWhenThrown = true });
                    }
                    catch (Exception ex)
                    {
                        OutputWindowLogger.Global.Warn($"[debug] could not read an exception setting in '{gs.Name}': {ex.Message}");
                    }
                }
            }

            list.Sort((a, b) =>
            {
                var g = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
                return g != 0 ? g : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return (true, list, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetExceptionSettingsAsync", ex);
            return (false, null, "Failed to read exception settings.");
        }
    }

    public async Task<(bool Ok, string Reason)> StartWithoutDebuggingAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = GetDte();
        if (dte == null) { return (false, "DTE unavailable."); }
        try
        {
            dte.ExecuteCommand("Debug.StartWithoutDebugging");
            return (true, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.StartWithoutDebuggingAsync", ex);
            return (false, "Failed to start without debugging.");
        }
    }

    /// <summary>Apply pending code edits to the running/paused program WITHOUT restarting it
    /// (Hot Reload / Edit-and-Continue). Changes the code, not just values. Only meaningful during
    /// a debug session; some edits ("rude edits": signature changes, etc.) can't be applied and
    /// require a restart — VS reports that in the output. Reads the result from the build/output.</summary>
    public async Task<DebugResult> ApplyHotReloadAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = GetDte();
            var dbg = dte?.Debugger;
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                return new DebugResult { Ok = false, Mode = "design", Reason = "Hot Reload needs a running debug session (start/attach first)." };
            }

            // Debug.ApplyCodeChanges = the Hot Reload command. ExecuteCommand throws if the
            // command is disabled (e.g. nothing to apply, or unsupported edit pending).
            dte.ExecuteCommand("Debug.ApplyCodeChanges", "");
            return new DebugResult
            {
                Ok = true,
                Mode = ModeToString(dbg.CurrentMode),
                Reason = "Applied code changes. Check the Output window (readOutput) for unsupported-edit warnings.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ApplyHotReloadAsync", ex);
            return new DebugResult { Ok = false, Reason = "Hot Reload failed or not available (no changes, or a rude edit needing restart)." };
        }
    }

    /// <summary>Configure break-when-thrown for an exception type (e.g. "System.NullReferenceException").
    /// Works in any mode. Group defaults to the CLR exceptions group when not given. Goes through
    /// EnvDTE90's Debugger3.ExceptionGroups, which the VS SDK metapackage already brings in.</summary>
    public async Task<DebugResult> SetExceptionBreakAsync(string exceptionName, bool breakWhenThrown, string group)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrWhiteSpace(exceptionName))
            {
                return new DebugResult { Ok = false, Reason = "exceptionName is required." };
            }

            // Cast, not reflection: DTE.Debugger hands back an RCW typed on the base interface, so
            // GetProperty("ExceptionGroups") — a Debugger3 member — found nothing and every call
            // failed as "not available", whatever the solution. Debugger3 is a GUID'd COM interface,
            // so it answers to QueryInterface and not to a lookup by name.
            var groups = (dbg as EnvDTE90.Debugger3)?.ExceptionGroups;
            if (groups == null)
            {
                OutputWindowLogger.Global.Debug(() => "[debug] Debugger3.ExceptionGroups unavailable — exception-break config skipped");
                return new DebugResult { Ok = false, Reason = "Exception settings not available on this debugger." };
            }

            var groupName = string.IsNullOrWhiteSpace(group) ? "Common Language Runtime Exceptions" : group;
            EnvDTE90.ExceptionSettings exGroup;
            // Item() on these COM collections throws on a missing key rather than returning null,
            // so the lookups are guarded here instead of null-checked.
            try { exGroup = groups.Item(groupName); }
            catch (Exception) { return new DebugResult { Ok = false, Reason = $"Exception group '{groupName}' not found." }; }

            EnvDTE90.ExceptionSetting exItem;
            try { exItem = exGroup.Item(exceptionName); }
            catch (Exception)
            {
                // VS only lists a well-known subset, so a correctly-named application exception is
                // absent — which is exactly the type worth breaking on. NewException adds the
                // setting, the same as typing it into the Exception Settings window.
                try { exItem = exGroup.NewException(exceptionName, 0); }
                catch (Exception ex)
                {
                    OutputWindowLogger.Global.Warn($"[debug] could not add exception setting '{exceptionName}': {ex.Message}");
                    return new DebugResult
                    {
                        Ok = false,
                        Reason = $"Exception '{exceptionName}' is not in group '{groupName}' and could not be added. " +
                                 "Pass the fully-qualified type name, and check the group is the right one.",
                    };
                }
            }

            exGroup.SetBreakWhenThrown(breakWhenThrown, exItem);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = $"Break-when-thrown {(breakWhenThrown ? "on" : "off")} for {exceptionName}." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.SetExceptionBreakAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set exception break." };
        }
    }


}

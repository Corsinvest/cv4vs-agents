/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
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
            OutputWindowLogger.LogException("IdeDebugService.GetStateAsync", ex);
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
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.StartAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to start debugging." };
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
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.StopAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to stop debugging." };
        }
    }

    /// <summary>Add a breakpoint at <paramref name="filePath"/>:<paramref name="line"/>, with an
    /// optional condition (true-expression). Works in any mode. Returns Ok even if VS can't bind
    /// it yet (it binds when the code loads), as long as the request was accepted.</summary>
    public async Task<DebugResult> SetBreakpointAsync(string filePath, int line, string condition)
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
                HitCount: 0,
                HitCountType: dbgHitCountType.dbgHitCountTypeNone);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.SetBreakpointAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set breakpoint." };
        }
    }

    /// <summary>Add a breakpoint that triggers on entry to a function by NAME (e.g.
    /// "MyNamespace.MyClass.Calculate"), instead of a file/line — handy when you know the method
    /// but not the line. Optional condition. Works in any mode.</summary>
    public async Task<DebugResult> SetFunctionBreakpointAsync(string functionName, string condition)
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
                HitCount: 0,
                HitCountType: dbgHitCountType.dbgHitCountTypeNone);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.SetFunctionBreakpointAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.RemoveBreakpointAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.ClearBreakpointsAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.ListBreakpointsAsync", ex);
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
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.BreakAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.ListProcessesAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.AttachAsync", ex);
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
                return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = "Was not debugging; started." };
            }
            // Debug.Restart command handles stop+start cleanly across project types.
            dte.ExecuteCommand("Debug.Restart", "");
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.RestartAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to restart debugging." };
        }
    }

    /// <summary>Start the program without the debugger (Ctrl+F5). If projectName is given, sets
    /// it as the startup project first via IdeContextService.</summary>
    public async Task<(bool Ok, string Reason)> StartWithoutDebuggingAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = GetDte();
        if (dte == null) { return (false, "DTE unavailable."); }
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var set = await IdeContextService.Instance.SetStartupProjectAsync(projectName);
            if (!set.Ok) { return (false, set.Reason); }
        }
        try
        {
            dte.ExecuteCommand("Debug.StartWithoutDebugging");
            return (true, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.StartWithoutDebuggingAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.ApplyHotReloadAsync", ex);
            return new DebugResult { Ok = false, Reason = "Hot Reload failed or not available (no changes, or a rude edit needing restart)." };
        }
    }

    /// <summary>Configure break-when-thrown for an exception type (e.g. "System.NullReferenceException").
    /// Works in any mode. Group defaults to the CLR exceptions group when not given. Uses the
    /// EnvDTE90 Debugger3.ExceptionGroups API via late binding so we don't hard-reference EnvDTE90.</summary>
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

            // dbg.ExceptionGroups (Debugger3) — reach via late binding to avoid an EnvDTE90 ref.
            var groups = dbg.GetType().GetProperty("ExceptionGroups")?.GetValue(dbg);
            if (groups == null)
            {
                OutputWindowLogger.Debug(() => "[debug] Debugger3.ExceptionGroups unavailable — exception-break config skipped");
                return new DebugResult { Ok = false, Reason = "Exception settings not available (no solution loaded?)." };
            }

            var groupName = string.IsNullOrWhiteSpace(group) ? "Common Language Runtime Exceptions" : group;
            var exGroup = VsReflection.Invoke(groups, "Item", [typeof(object)], [groupName]);
            if (exGroup == null)
            {
                return new DebugResult { Ok = false, Reason = $"Exception group '{groupName}' not found." };
            }

            // group.Item(exceptionName) → the ExceptionSetting; then group.SetBreakWhenThrown(flag, setting).
            var exItem = VsReflection.Invoke(exGroup, "Item", [typeof(object)], [exceptionName]);
            if (exItem == null)
            {
                return new DebugResult { Ok = false, Reason = $"Exception '{exceptionName}' not found in group '{groupName}'." };
            }

            VsReflection.Invoke(exGroup, "SetBreakWhenThrown", (object)breakWhenThrown, exItem);
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), Reason = $"Break-when-thrown {(breakWhenThrown ? "on" : "off")} for {exceptionName}." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.SetExceptionBreakAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to set exception break." };
        }
    }


}

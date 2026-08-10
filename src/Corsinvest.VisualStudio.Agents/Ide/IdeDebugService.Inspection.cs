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
/// IdeDebugService, live-inspection side: the operations that only work while the
/// debuggee is paused (continue, step, call stack, locals, evaluate). Session control
/// and breakpoints live in IdeDebugService.cs; shared helpers in IdeDebugService.Common.cs.
/// </summary>
internal sealed partial class IdeDebugService
{
    // Live inspection + stepping: every entry point here requires Break mode.

    private const string NotInBreak =
        "Debugger must be paused (break mode) for this. Poll getDebugState and wait for mode='break' " +
        "(set a breakpoint then start/continue, or call debugBreak).";

    /// <summary>Resume execution from a break (like F5 while paused).</summary>
    public async Task<DebugResult> ContinueAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new DebugResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new DebugResult { Ok = false, Mode = ModeToString(dbg.CurrentMode), Reason = NotInBreak };
            }
            dbg.Go(false);
            // No Mode: Go() returns before the transition, so this reads the state being left. It
            // happened to be right — "run" either way — which is why it outlived the same fix on
            // start/stop/restart/break.
            return new DebugResult { Ok = true, Reason = "Resumed — poll getDebugState for where it stops next." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ContinueAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to continue." };
        }
    }

    /// <summary>Step over/into/out (only while paused). Returns the new location.</summary>
    public async Task<StepResult> StepAsync(string direction)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new StepResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new StepResult { Ok = false, Mode = ModeToString(dbg.CurrentMode), Reason = NotInBreak };
            }

            switch ((direction ?? "over").ToLowerInvariant())
            {
                case "into": dbg.StepInto(false); break;
                case "out": dbg.StepOut(false); break;
                case "over":
                default: dbg.StepOver(false); break;
            }

            // After stepping, VS is usually back in break at the new statement.
            var (file, line) = CurrentLocation();
            return new StepResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), File = file, Line = line };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.StepAsync", ex);
            return new StepResult { Ok = false, Reason = "Failed to step." };
        }
    }

    /// <summary>Call stack of the current thread (only while paused).</summary>
    public async Task<CallStackResult> GetCallStackAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new CallStackResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new CallStackResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            var frames = new List<StackFrameInfo>();
            var thread = dbg.CurrentThread;
            var currentIndex = -1;
            if (thread != null)
            {
                // Found by walking the collection rather than comparing objects: COM hands back a
                // fresh wrapper per call, and even IUnknown came back different for the frame that
                // WAS selected, so every frame read as "not current". StackFrames is 1-based.
                var current = dbg.CurrentStackFrame;
                var i = 0;
                foreach (StackFrame sf in thread.StackFrames)
                {
                    if (currentIndex < 0 && current != null && SameFrame(sf, current)) { currentIndex = i; }
                    frames.Add(new StackFrameInfo
                    {
                        Index = i++,
                        Function = sf.FunctionName,
                        Module = SafeModule(sf),
                        // EnvDTE StackFrame has no file/line; those come from the active doc, which
                        // follows the SELECTED frame — so they go on that one, below.
                    });
                }
            }
            if (currentIndex < 0 && frames.Count > 0) { currentIndex = 0; }
            if (currentIndex >= 0)
            {
                frames[currentIndex].IsCurrent = true;
                // The editor shows where the selected frame is, not where execution is paused —
                // putting this on frame 0 after a select_frame gave it the caller's line.
                var (file, line) = CurrentLocation();
                frames[currentIndex].File = file;
                frames[currentIndex].Line = line;
            }
            return new CallStackResult { Ok = true, InBreak = true, Frames = [.. frames] };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetCallStackAsync", ex);
            return new CallStackResult { Ok = false, Reason = "Failed to get call stack." };
        }
    }

    /// <summary>The parameters and locals of the selected frame (only while paused).
    /// <para><paramref name="depth"/> 0 keeps the flat list this always returned; above that the
    /// members are walked as debug_expand does, so "the locals, one level down" is one call rather
    /// than a get_locals followed by an expand per object.</para></summary>
    public async Task<LocalsResult> GetLocalsAsync(int depth, int maxMembers)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new LocalsResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new LocalsResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            var frame = dbg.CurrentStackFrame;
            if (frame == null) { return new LocalsResult { Ok = false, InBreak = true, Reason = "No current stack frame." }; }

            var locals = new List<LocalInfo>();
            var truncated = false;
            // Arguments are a separate collection on the frame, and were missing entirely: stopped
            // in a method, its parameters are usually the first thing worth reading.
            Collect(frame.Arguments, isArgument: true);
            Collect(frame.Locals, isArgument: false);

            void Collect(Expressions source, bool isArgument)
            {
                if (source == null) { return; }
                foreach (Expression e in source)
                {
                    var hasMembers = e.DataMembers?.Count > 0;
                    locals.Add(new LocalInfo
                    {
                        Name = e.Name,
                        Type = e.Type,
                        Value = e.Value,
                        HasMembers = hasMembers,
                        IsArgument = isArgument,
                        Members = hasMembers && depth > 0 ? WalkMembers(e, depth, maxMembers, ref truncated) : null,
                    });
                }
            }

            // Arguments first, then locals, each by name: the parameters are what the caller passed
            // in, so they read as the frame's header rather than as more variables.
            var ordered = locals.OrderByDescending(l => l.IsArgument)
                                .ThenBy(l => l.Name, StringComparer.Ordinal)
                                .ToArray();
            return new LocalsResult
            {
                Ok = true,
                InBreak = true,
                FunctionName = frame.FunctionName,
                Locals = ordered,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetLocalsAsync", ex);
            return new LocalsResult { Ok = false, Reason = "Failed to get locals." };
        }
    }

    /// <summary>The threads of the program being debugged (only while paused). Everything else here
    /// reads one thread — the current one — and until this there was no way to learn that the others
    /// existed, let alone name them.</summary>
    public async Task<ThreadsResult> GetThreadsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new ThreadsResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new ThreadsResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            // Threads hang off the program, not off the debugger: Debugger.CurrentProgram.Threads.
            var program = dbg.CurrentProgram;
            if (program == null) { return new ThreadsResult { Ok = false, InBreak = true, Reason = "No program is being debugged." }; }

            var currentId = SafeThreadId(dbg.CurrentThread);
            var threads = new List<ThreadInfo>();
            foreach (EnvDTE.Thread t in program.Threads)
            {
                var name = SafeName(t);
                var location = SafeLocation(t);
                threads.Add(new ThreadInfo
                {
                    Id = t.ID,
                    // Most threads have no Name: it is set in code and hardly anyone does, the main
                    // thread included. Falling back to the location keeps every row identifiable —
                    // "which one is Main" was otherwise answerable only by reading two fields.
                    Name = !string.IsNullOrEmpty(name) ? name : (location ?? "(unnamed)"),
                    Location = location ?? "(no managed frames)",
                    IsAlive = t.IsAlive,
                    IsFrozen = t.IsFrozen,
                    IsCurrent = currentId != 0 && t.ID == currentId,
                });
            }
            return new ThreadsResult { Ok = true, InBreak = true, Threads = [.. threads.OrderBy(t => t.Id)] };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetThreadsAsync", ex);
            return new ThreadsResult { Ok = false, Reason = "Failed to list the threads." };
        }
    }

    /// <summary>Make <paramref name="threadId"/> the thread the call stack and the inspection tools
    /// read. The frame selection resets with it — frames belong to a thread.</summary>
    public async Task<ThreadActionResult> SelectThreadAsync(int threadId)
        => await WithThreadAsync(threadId, (dbg, t) =>
        {
            dbg.CurrentThread = t;
            return new ThreadActionResult { Ok = true, InBreak = true, ThreadId = t.ID, Name = t.Name, IsFrozen = t.IsFrozen };
        });

    /// <summary>Freeze or thaw one thread. A frozen thread does not run when the program resumes,
    /// which is how a race is pinned down: freeze the others, step the one being watched.</summary>
    public async Task<ThreadActionResult> FreezeThreadAsync(int threadId, bool freeze)
        => await WithThreadAsync(threadId, (dbg, t) =>
        {
            if (freeze) { t.Freeze(); } else { t.Thaw(); }
            return new ThreadActionResult { Ok = true, InBreak = true, ThreadId = t.ID, Name = t.Name, IsFrozen = t.IsFrozen };
        });

    /// <summary>Find a thread by id and act on it. Shared because the two callers differ only in
    /// what they do once they have it, and the four ways of not having it are the same.</summary>
    private async Task<ThreadActionResult> WithThreadAsync(int threadId, Func<Debugger, EnvDTE.Thread, ThreadActionResult> action)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new ThreadActionResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new ThreadActionResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }
            var program = dbg.CurrentProgram;
            if (program == null) { return new ThreadActionResult { Ok = false, InBreak = true, Reason = "No program is being debugged." }; }

            foreach (EnvDTE.Thread t in program.Threads)
            {
                if (t.ID == threadId) { return action(dbg, t); }
            }
            var ids = string.Join(", ", program.Threads.Cast<EnvDTE.Thread>().Select(t => t.ID));
            return new ThreadActionResult
            {
                Ok = false,
                InBreak = true,
                Reason = $"No thread {threadId} — the program has {ids}. debug_list_threads shows them with their locations.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.WithThreadAsync", ex);
            return new ThreadActionResult { Ok = false, Reason = "Failed to reach that thread." };
        }
    }

    /// <summary>A thread's id, or 0 when it can't be read — CurrentThread is null between some
    /// transitions, and asking a dead thread for its id throws.</summary>
    private static int SafeThreadId(EnvDTE.Thread t)
    {
        try { return t?.ID ?? 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>Where a thread is. Reading it can throw on a thread with no managed frames, which is
    /// no reason to lose the rest of the list.</summary>
    private static string SafeLocation(EnvDTE.Thread t)
    {
        try { return string.IsNullOrWhiteSpace(t.Location) ? null : t.Location; }
        catch (Exception) { return null; }
    }

    /// <summary>A thread's name, or null. Same guard as the location: a thread the debugger cannot
    /// fully see throws rather than answering.</summary>
    private static string SafeName(EnvDTE.Thread t)
    {
        try { return string.IsNullOrWhiteSpace(t.Name) ? null : t.Name; }
        catch (Exception) { return null; }
    }

    /// <summary>Point the inspection tools at another frame of the current call stack (only while
    /// paused). Locals belong to a frame, so stopped inside a callee the caller's variables are out
    /// of scope until this moves the selection — the debugger's own Call Stack window does the same
    /// on a double click.</summary>
    public async Task<SelectFrameResult> SelectFrameAsync(int index)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new SelectFrameResult { Ok = false, Reason = "Debugger not available." }; }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new SelectFrameResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            var thread = dbg.CurrentThread;
            if (thread == null) { return new SelectFrameResult { Ok = false, InBreak = true, Reason = "No current thread." }; }

            var frames = thread.StackFrames;
            var count = frames?.Count ?? 0;
            if (index < 0 || index >= count)
            {
                return new SelectFrameResult
                {
                    Ok = false,
                    InBreak = true,
                    Reason = $"Frame {index} is out of range — the stack has {count} ({(count > 0 ? $"0..{count - 1}" : "none")}).",
                };
            }

            // StackFrames is 1-based, unlike the index the call stack is reported with.
            var target = frames.Item(index + 1);
            dbg.CurrentStackFrame = target;
            return new SelectFrameResult
            {
                Ok = true,
                InBreak = true,
                Index = index,
                Function = target.FunctionName,
                FrameCount = count,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.SelectFrameAsync", ex);
            return new SelectFrameResult { Ok = false, Reason = "Failed to select the frame." };
        }
    }

    /// <summary>Evaluate an expression in the current frame (only while paused). Read-oriented, but
    /// note it can call getters/methods in the debuggee (side-effects possible).</summary>
    public async Task<EvalResult> EvaluateAsync(string expression)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new EvalResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrWhiteSpace(expression))
            {
                return new EvalResult { Ok = false, Reason = "expression is required." };
            }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new EvalResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            var ex = dbg.GetExpression(expression, true, -1);
            return new EvalResult
            {
                Ok = true,
                InBreak = true,
                Expression = expression,
                Value = ex.Value,
                Type = ex.Type,
                IsValid = ex.IsValidValue,
                Reason = ex.IsValidValue ? null : NotInScopeReason(dbg),
            };
        }
        catch (Exception exc)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.EvaluateAsync", exc);
            return new EvalResult { Ok = false, Reason = "Failed to evaluate." };
        }
    }

    /// <summary>Evaluate an expression and walk its members, so an object comes back as a tree
    /// instead of a type name. Same evaluation as <see cref="EvaluateAsync"/> — and the same
    /// caveat: reading a property runs its getter in the debuggee.</summary>
    public async Task<ExpandResult> ExpandAsync(string expression, int depth, int maxMembers)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dbg = GetDebugger();
            if (dbg == null) { return new ExpandResult { Ok = false, Reason = "Debugger not available." }; }
            if (string.IsNullOrWhiteSpace(expression))
            {
                return new ExpandResult { Ok = false, Reason = "expression is required." };
            }
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode)
            {
                return new ExpandResult { Ok = false, InBreak = false, Reason = NotInBreak };
            }

            var root = dbg.GetExpression(expression, true, -1);
            if (!root.IsValidValue)
            {
                return new ExpandResult
                {
                    Ok = false,
                    InBreak = true,
                    Expression = expression,
                    Reason = NotInScopeReason(dbg),
                };
            }

            var truncated = false;
            return new ExpandResult
            {
                Ok = true,
                InBreak = true,
                Expression = expression,
                Value = root.Value,
                Type = root.Type,
                Members = WalkMembers(root, depth, maxMembers, ref truncated),
                Truncated = truncated,
            };
        }
        catch (Exception exc)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ExpandAsync", exc);
            return new ExpandResult { Ok = false, Reason = "Failed to expand." };
        }
    }

    /// <summary>Whether two StackFrame wrappers stand for the same frame. Object identity does not
    /// answer this — not ReferenceEquals, and not IUnknown either, which came back different for
    /// the frame that was selected. So it compares what a frame is made of: the function, the
    /// module, and the return type that separates two overloads of the same name.</summary>
    private static bool SameFrame(StackFrame a, StackFrame b)
    {
        try
        {
            return string.Equals(a.FunctionName, b.FunctionName, StringComparison.Ordinal)
                && string.Equals(SafeModule(a), SafeModule(b), StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ReturnType, b.ReturnType, StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Why an expression didn't resolve. Naming the frame separates the three ways this
    /// fails — a wrong name, a variable not declared yet, and one that is alive a frame up — which
    /// otherwise read the same, and the third is the common case when stopped inside a callee.</summary>
    private static string NotInScopeReason(Debugger dbg)
    {
        var frame = dbg.CurrentStackFrame?.FunctionName;
        return string.IsNullOrEmpty(frame)
            ? "Expression not valid in the current scope."
            : $"Not in scope in the current frame ({frame}) — it may be a local of a calling frame: "
              + "debug_get_callstack lists them, debug_select_frame switches.";
    }

    /// <summary>One level of members, recursing while <paramref name="depth"/> is left. Sorted by
    /// name like the locals list, so two reads of the same object line up.</summary>
    private static LocalInfo[] WalkMembers(Expression parent, int depth, int maxMembers, ref bool truncated)
    {
        if (depth <= 0) { return null; }

        var members = parent.DataMembers;
        if (members == null || members.Count == 0) { return null; }
        if (members.Count > maxMembers) { truncated = true; }

        var taken = new List<LocalInfo>();
        foreach (Expression m in members)
        {
            if (taken.Count >= maxMembers) { break; }
            var hasMembers = m.DataMembers?.Count > 0;
            taken.Add(new LocalInfo
            {
                Name = m.Name,
                Type = m.Type,
                Value = m.Value,
                HasMembers = hasMembers,
                Members = hasMembers ? WalkMembers(m, depth - 1, maxMembers, ref truncated) : null,
            });
        }
        return [.. taken.OrderBy(l => l.Name, StringComparer.Ordinal)];
    }
}

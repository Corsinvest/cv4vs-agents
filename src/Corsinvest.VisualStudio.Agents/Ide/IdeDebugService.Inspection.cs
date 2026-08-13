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
        "Debugger must be paused (break mode) for this. Poll debug_get_state and wait for mode='break' " +
        "(set a breakpoint then start/continue, or call debug_break).";

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
            // This break is over: the next one may land on another thread, and nothing guarantees a
            // read in between to notice the program ran.
            _stoppedThreadId = 0;
            // No Mode: Go() returns before the transition, so this reads the state being left. It
            // happened to be right — "run" either way — which is why it outlived the same fix on
            // start/stop/restart/break.
            return new DebugResult { Ok = true, Reason = "Resumed — poll debug_get_state for where it stops next." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.ContinueAsync", ex);
            return new DebugResult { Ok = false, Reason = "Failed to continue." };
        }
    }

    /// <summary>How long to wait for a step to land before answering without a position. Long
    /// enough for a step over a slow call, short enough that a step which will not finish — into a
    /// blocking read, say — does not hold the caller.</summary>
    private static readonly TimeSpan StepSettleTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Step over/into/out (only while paused), waiting for it to land so the location is
    /// where it arrived.
    /// <para>The wait is the whole point. StepOver(false) and friends return immediately, and
    /// reading the position straight after gave the one it STARTED from, every time — the mode came
    /// back "run" for the same reason. Measured on a step out of a method with a second of work
    /// left: it answered Seed.cs:64 while the program went on to Program.cs:19.</para></summary>
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

            // An unknown direction is refused rather than treated as "over". The schema declares
            // the three values, but nothing enforces it on the way in — and stepping over a line
            // when the caller asked to step into it is a wrong answer disguised as a right one.
            switch ((direction ?? "over").ToLowerInvariant())
            {
                case "into": dbg.StepInto(false); break;
                case "out": dbg.StepOut(false); break;
                case "over": dbg.StepOver(false); break;
                default:
                    return new StepResult
                    {
                        Ok = false,
                        Mode = ModeToString(dbg.CurrentMode),
                        Reason = $"Unknown direction '{direction}'. Use 'over', 'into' or 'out'. Nothing was stepped.",
                    };
            }

            // The step leaves this break: whatever it lands on is a new one, on a thread this has no
            // say over. Cleared so the wait below records the landing rather than keeping the old id.
            _stoppedThreadId = 0;

            var landed = await WaitForBreakAsync(StepSettleTimeout);
            if (!landed)
            {
                // Still running: the step is over something that has not finished, or the program
                // went on to another breakpoint. No position rather than the old one.
                return new StepResult
                {
                    Ok = true,
                    Reason = "Step still running after 10s — poll debug_get_state for where it stops.",
                };
            }

            var (file, line) = CurrentLocation();
            return new StepResult { Ok = true, Mode = ModeToString(dbg.CurrentMode), File = file, Line = line };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.StepAsync", ex);
            return new StepResult { Ok = false, Reason = "Failed to step." };
        }
    }

    /// <summary>The thread the debugger stopped on, or 0 when not stopped. CurrentLocation() answers
    /// for that one thread — the breakpoint that was hit, or the caret VS brought to the suspension
    /// point — so only its frames may carry a file and line.
    ///
    /// Recorded rather than derived because debug_select_thread moves Debugger.CurrentThread and
    /// nothing remembers where the break landed. Tracking the thread the CALLER selected instead was
    /// wrong in the symmetric case: selecting the stopping thread explicitly made it match too, and
    /// the position was withheld from the one thread entitled to it.</summary>
    private static int _stoppedThreadId;

    /// <summary>Note which thread a break landed on, and forget it once the program runs again.
    /// Called from GetDebugger(), so it runs before whatever the entry point came to do — including
    /// the debug_select_thread that moves CurrentThread away from the answer.
    /// <para>Only the first read while stopped counts: after that CurrentThread is whatever the
    /// caller selected, and taking it again would record the choice instead of the break.</para></summary>
    private static void TrackStoppedThread(Debugger dbg)
    {
        if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode) { _stoppedThreadId = 0; return; }
        if (_stoppedThreadId == 0) { _stoppedThreadId = SafeThreadId(dbg.CurrentThread); }
    }

    /// <summary>Wait for the debugger to be back in break, or give up. Yields the UI thread between
    /// looks — the transition is processed there, so holding it would stop the very thing being
    /// waited for. Returns false on timeout.</summary>
    private static async Task<bool> WaitForBreakAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dbg = GetDebugger();
            if (dbg == null) { return false; }
            if (dbg.CurrentMode == dbgDebugMode.dbgBreakMode) { return true; }
            // Design mode: the program ended while stepping — nothing left to land on.
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode) { return false; }
        }
        return false;
    }

    /// <summary>Call stack of the current thread (only while paused).</summary>
    /// <summary>The call stack of one thread by id, WITHOUT selecting it. Reading another thread's
    /// stack otherwise means debug_select_thread first, which moves the debugger's current thread —
    /// the user's Call Stack and Locals windows follow it, and nothing puts them back.
    ///
    /// No frame carries file/line here: EnvDTE reads those from the active document, which follows
    /// the SELECTED frame, so they would describe the wrong thread entirely.</summary>
    public async Task<CallStackResult> GetThreadCallStackAsync(int threadId)
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

            Thread match = null;
            var known = new List<int>();
            foreach (Thread t in dbg.CurrentProgram?.Threads ?? (Threads)null)
            {
                known.Add(t.ID);
                if (t.ID == threadId) { match = t; break; }
            }
            if (match == null)
            {
                known.Sort();
                return new CallStackResult
                {
                    Ok = false,
                    InBreak = true,
                    Reason = $"No thread with id {threadId}. Known ids: {string.Join(", ", known)} — debug_list_threads has the details.",
                };
            }

            var frames = new List<StackFrameInfo>();
            var i = 0;
            foreach (StackFrame sf in match.StackFrames)
            {
                frames.Add(new StackFrameInfo
                {
                    Index = i++,
                    Function = sf.FunctionName,
                    Module = SafeModule(sf),
                });
            }
            return new CallStackResult { Ok = true, InBreak = true, Frames = [.. frames] };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDebugService.GetThreadCallStackAsync", ex);
            return new CallStackResult { Ok = false, Reason = "Failed to read the thread's call stack." };
        }
    }

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
            // No match means the selected frame belongs to a DIFFERENT thread than the one whose
            // stack this is. Falling back to frame 0 used to hang the editor's position on it —
            // a worker blocked in WaitOne came back reading Program.cs:27, which is where the main
            // thread was paused. Better to mark no frame current than to name the wrong file.
            if (currentIndex >= 0)
            {
                frames[currentIndex].IsCurrent = true;
                // Only for the thread the debugger stopped on. CurrentLocation answers with the
                // breakpoint that was hit or the editor's caret, and both belong to that one
                // thread: asked after a debug_select_thread onto a worker, it named the main
                // thread's line as if it were the worker's. Other threads get no position —
                // EnvDTE's StackFrame carries none of its own, and none beats someone else's.
                if (thread.ID == _stoppedThreadId)
                {
                    var (file, line) = CurrentLocation();
                    frames[currentIndex].File = file;
                    frames[currentIndex].Line = line;
                }
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
                    // Top-level statements report `args` in BOTH collections, so it arrived twice —
                    // once flagged as an argument, once not, which reads as a bug in the tool.
                    // Arguments are collected first and win: knowing a name is a parameter says
                    // more than knowing it is in scope.
                    if (!isArgument && locals.Exists(l => string.Equals(l.Name, e.Name, StringComparison.Ordinal)))
                    {
                        continue;
                    }
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
                    // thread included. Said plainly rather than filled with the location, which
                    // only printed the same string in both columns.
                    Name = name ?? "(unnamed)",
                    Location = location ?? "(no managed frames)",
                    IsAlive = t.IsAlive,
                    IsFrozen = t.IsFrozen,
                    IsCurrent = currentId != 0 && t.ID == currentId,
                });
            }
            // Current first: in a dozen rows that read alike, a boolean at the end of one of them
            // has to be hunted for. Position says it before the field does.
            return new ThreadsResult
            {
                Ok = true,
                InBreak = true,
                Threads = [.. threads.OrderByDescending(t => t.IsCurrent).ThenBy(t => t.Id)],
            };
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
            // InBreak stays true: we got past the mode check to reach here, and reporting false
            // would send the caller off to wait for a break it is already in. VS refuses a frame it
            // has no symbols for — a runtime or OS frame — and its own message is the only thing
            // that says which frame and why.
            return new SelectFrameResult
            {
                Ok = false,
                InBreak = true,
                Reason = $"Could not select frame {index}: {ex.Message} " +
                         "Frames with no symbols — runtime and OS ones — cannot be selected; " +
                         "debug_get_callstack shows which have a module.",
            };
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

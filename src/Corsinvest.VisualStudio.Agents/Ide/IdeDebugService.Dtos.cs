/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeDebugService result DTOs: the structured records every debugger operation
/// returns (state, breakpoints, processes, locals, call stack, eval, step). The
/// operations themselves live in IdeDebugService.cs.
/// </summary>
internal sealed partial class IdeDebugService
{
    /// <summary>Current debugger state. Mode is "design" (not debugging), "run" (running),
    /// or "break" (paused on a breakpoint/exception — the only mode where live inspection
    /// works). CurrentFile/CurrentLine are set only in break mode.</summary>
    public sealed class DebugState
    {
        public string Mode { get; set; }          // design | run | break | unknown
        public string CurrentFile { get; set; }   // in break mode: file of the active stack frame
        public int CurrentLine { get; set; }      // 1-based; 0 when unknown
        // Set only when paused ON AN EXCEPTION (read from the $exception pseudo-variable).
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
    }

    public sealed class DebugResult
    {
        public bool Ok { get; set; }
        public string Mode { get; set; }
        public string Reason { get; set; }

        /// <summary>How many code locations a breakpoint resolved to, or null outside a debug
        /// session, where nothing has bound yet and 0 would read as a failure. 0 during a session
        /// means the breakpoint will not stop anything. Only set by the breakpoint tools.</summary>
        public int? Bound { get; set; }

        /// <summary>Where the breakpoint landed. For a file breakpoint that is always the line asked
        /// for — VS rejects a line it can't use rather than moving it — but for a function breakpoint
        /// it is the only way the caller learns which file and line the name resolved to. A null line
        /// means unresolved, which in design mode is normal. Only set by the breakpoint tools.</summary>
        public string File { get; set; }
        public int? Line { get; set; }
    }

    /// <summary>One breakpoint in the solution (file/line OR function, plus condition/enabled).</summary>
    public sealed class BreakpointInfo
    {
        public string File { get; set; }       // set for file breakpoints
        public int Line { get; set; }          // 1-based; 0 for function breakpoints
        public string Function { get; set; }   // set for function breakpoints
        public string Condition { get; set; }  // null when unconditional
        public bool Enabled { get; set; }

        /// <summary>The hit-count rule, when there is one. Without these a breakpoint set to stop on
        /// the 500th pass reads exactly like one that stops every time — and "why did it not break"
        /// is the question this list exists to answer.</summary>
        public int HitCount { get; set; }
        public string HitCountType { get; set; }   // null unless HitCount is set

        /// <summary>Times it has been hit in this session — the difference between "never reached"
        /// and "reached, and the condition said no".</summary>
        public int CurrentHits { get; set; }

        /// <summary>Code locations this breakpoint resolved to, or null outside a session where
        /// nothing has bound yet. The third answer to "why did it not break", after the hit count:
        /// 0 means it never will, because the line holds no code or the module's symbols are not
        /// loaded — as opposed to bound and simply not reached.</summary>
        public int? Bound { get; set; }
    }

    public sealed class BreakpointsResult
    {
        public bool Ok { get; set; }
        public BreakpointInfo[] Breakpoints { get; set; } = [];
        public string Reason { get; set; }
    }

    /// <summary>A local process the debugger could attach to.</summary>
    public sealed class ProcessInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public bool BeingDebugged { get; set; }
    }

    public sealed class ProcessesResult
    {
        public bool Ok { get; set; }
        public ProcessInfo[] Processes { get; set; } = [];
        public string Reason { get; set; }
    }

    /// <summary>A local variable in the current frame. Members aren't expanded — HasMembers tells
    /// the model it can drill in with evaluateExpression on "name.member".</summary>
    public sealed class LocalInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        /// <summary>True when the value has members. Kept on expanded nodes too: at the depth limit
        /// it is what says "there is more below — expand this path".</summary>
        public bool HasMembers { get; set; }
        /// <summary>Filled by ExpandAsync only, and null (not empty) where nothing was walked —
        /// either a leaf or the depth limit.</summary>
        public LocalInfo[] Members { get; set; }
        /// <summary>True for a parameter the caller passed, false for a variable the method
        /// declared. Only set by GetLocalsAsync — an expanded member is neither.</summary>
        public bool IsArgument { get; set; }
    }

    public sealed class LocalsResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }   // false ⇒ not paused; the model should poll debug_get_state
        public string FunctionName { get; set; }
        public LocalInfo[] Locals { get; set; } = [];
        /// <summary>Set when a walked level hit the member cap — only possible with depth &gt; 0.</summary>
        public bool Truncated { get; set; }
        public string Reason { get; set; }
    }

    public sealed class StackFrameInfo
    {
        /// <summary>0 = where execution is paused, counting outwards to the caller. What
        /// debug_select_frame takes.</summary>
        public int Index { get; set; }
        public string Function { get; set; }
        public string Module { get; set; }
        public string File { get; set; }
        public int Line { get; set; }    // 1-based; 0 when unknown
        /// <summary>The frame the other inspection tools read. Index 0 until something selects
        /// another one.</summary>
        public bool IsCurrent { get; set; }
    }

    public sealed class CallStackResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public StackFrameInfo[] Frames { get; set; } = [];
        public string Reason { get; set; }
    }

    public sealed class EvalResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public string Expression { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public bool IsValid { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ThreadInfo
    {
        /// <summary>OS thread id — what debug_select_thread and debug_freeze_thread take.</summary>
        public int Id { get; set; }
        public string Name { get; set; }
        /// <summary>Where the thread is, as the Threads window shows it — usually the top frame's
        /// function.</summary>
        public string Location { get; set; }
        public bool IsAlive { get; set; }
        /// <summary>Frozen threads do not run when the program resumes, and stay that way until
        /// something thaws them.</summary>
        public bool IsFrozen { get; set; }
        /// <summary>The thread the call stack and the inspection tools read.</summary>
        public bool IsCurrent { get; set; }
    }

    public sealed class ThreadsResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public ThreadInfo[] Threads { get; set; } = [];
        public string Reason { get; set; }
    }

    /// <summary>What acting on one thread reports back — the thread it found, and its state after.
    /// </summary>
    public sealed class ThreadActionResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public int ThreadId { get; set; }
        public string Name { get; set; }
        public bool IsFrozen { get; set; }
        public string Reason { get; set; }
    }

    public sealed class SelectFrameResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public int Index { get; set; }
        public string Function { get; set; }
        public int FrameCount { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ExpandResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }
        public string Expression { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public LocalInfo[] Members { get; set; } = [];
        /// <summary>True when some level hit the member cap, so what came back is a prefix of what
        /// is there. Without it a truncated collection reads as a complete one.</summary>
        public bool Truncated { get; set; }
        public string Reason { get; set; }
    }

    public sealed class StepResult
    {
        public bool Ok { get; set; }
        public string Mode { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>One exception type the debugger is set to break on.</summary>
    public sealed class ExceptionBreakSetting
    {
        public string Group { get; set; }
        public string Name { get; set; }
        public bool BreakWhenThrown { get; set; }
    }
}

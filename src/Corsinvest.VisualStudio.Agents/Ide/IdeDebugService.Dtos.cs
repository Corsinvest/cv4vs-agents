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
    }

    /// <summary>One breakpoint in the solution (file/line OR function, plus condition/enabled).</summary>
    public sealed class BreakpointInfo
    {
        public string File { get; set; }       // set for file breakpoints
        public int Line { get; set; }          // 1-based; 0 for function breakpoints
        public string Function { get; set; }   // set for function breakpoints
        public string Condition { get; set; }  // null when unconditional
        public bool Enabled { get; set; }
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
        public bool HasMembers { get; set; }
    }

    public sealed class LocalsResult
    {
        public bool Ok { get; set; }
        public bool InBreak { get; set; }   // false ⇒ not paused; the model should poll getDebugState
        public string FunctionName { get; set; }
        public LocalInfo[] Locals { get; set; } = [];
        public string Reason { get; set; }
    }

    public sealed class StackFrameInfo
    {
        public string Function { get; set; }
        public string Module { get; set; }
        public string File { get; set; }
        public int Line { get; set; }    // 1-based; 0 when unknown
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

    public sealed class StepResult
    {
        public bool Ok { get; set; }
        public string Mode { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string Reason { get; set; }
    }
}

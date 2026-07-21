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
    // ---- Live inspection + stepping (require Break mode) ---------------------------

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
            return new DebugResult { Ok = true, Mode = ModeToString(dbg.CurrentMode) };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.ContinueAsync", ex);
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
            OutputWindowLogger.LogException("IdeDebugService.StepAsync", ex);
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
            if (thread != null)
            {
                foreach (StackFrame sf in thread.StackFrames)
                {
                    frames.Add(new StackFrameInfo
                    {
                        Function = sf.FunctionName,
                        Module = SafeModule(sf),
                        // EnvDTE StackFrame has no file/line; those come from the active doc for the
                        // top frame only. Leave 0 for deeper frames (function name is the key info).
                    });
                }
            }
            // Top frame: enrich with the current file/line VS shows.
            if (frames.Count > 0)
            {
                var (file, line) = CurrentLocation();
                frames[0].File = file;
                frames[0].Line = line;
            }
            return new CallStackResult { Ok = true, InBreak = true, Frames = [.. frames] };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.GetCallStackAsync", ex);
            return new CallStackResult { Ok = false, Reason = "Failed to get call stack." };
        }
    }

    /// <summary>Local variables of the current frame (only while paused). Members aren't expanded;
    /// drill in with evaluateExpression("name.member").</summary>
    public async Task<LocalsResult> GetLocalsAsync()
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
            foreach (Expression e in frame.Locals)
            {
                locals.Add(new LocalInfo
                {
                    Name = e.Name,
                    Type = e.Type,
                    Value = e.Value,
                    HasMembers = e.DataMembers?.Count > 0,
                });
            }
            var ordered = locals.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray();
            return new LocalsResult { Ok = true, InBreak = true, FunctionName = frame.FunctionName, Locals = ordered };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDebugService.GetLocalsAsync", ex);
            return new LocalsResult { Ok = false, Reason = "Failed to get locals." };
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
                Reason = ex.IsValidValue ? null : "Expression not valid in the current scope.",
            };
        }
        catch (Exception exc)
        {
            OutputWindowLogger.LogException("IdeDebugService.EvaluateAsync", exc);
            return new EvalResult { Ok = false, Reason = "Failed to evaluate." };
        }
    }
}

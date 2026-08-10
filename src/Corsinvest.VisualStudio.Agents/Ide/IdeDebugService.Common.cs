/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeDebugService shared helpers used by both the control/breakpoint side and the
/// live-inspection side: DTE/Debugger access, mode formatting, the paused location,
/// and value cleanup.
/// </summary>
internal sealed partial class IdeDebugService
{
    private static DTE GetDte() => Package.GetGlobalService(typeof(DTE)) as DTE;
    private static Debugger GetDebugger() => GetDte()?.Debugger;

    private static string ModeToString(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design",
        dbgDebugMode.dbgRunMode => "run",
        dbgDebugMode.dbgBreakMode => "break",
        _ => "unknown",
    };

    /// <summary>Where execution is paused: file + 1-based line.
    /// <para>When a breakpoint is what stopped us, it knows its own File/FileLine and that is the
    /// answer — the editor's caret is not. It reads as the same place most of the time, but
    /// run_to_line and set_next_statement move the caret themselves (the VS commands work off the
    /// selection), so after either of those the "position" was the one we had just put there.
    /// Measured: a break on line 19 reported as line 17, a comment.</para>
    /// <para>Everything else — a step, a thrown exception, Break All — has no such record, and
    /// falls back to the active document. That is the case where VS moves the caret itself, so it
    /// is the one where the caret is right.</para></summary>
    private static (string file, int line) CurrentLocation()
    {
        var fromBreakpoint = BreakpointLocation();
        if (fromBreakpoint.line > 0) { return fromBreakpoint; }

        try
        {
            var doc = GetDte()?.ActiveDocument;
            var file = doc?.FullName;
            var line = doc?.Selection is TextSelection sel ? sel.ActivePoint.Line : 0;
            return (file, line);
        }
        catch (Exception ex)
        {
            // Not a per-item probe: this is where the debugger stopped, and it feeds StepAsync and
            // GetCallStackAsync. Degrading to (null, 0) reads as "no location" — the same answer a
            // session with no active document gives — so without this the COM failure behind it
            // would leave nothing to read.
            OutputWindowLogger.Global.LogException("IdeDebugService.CurrentLocation", ex);
            return (null, 0);
        }
    }

    /// <summary>The breakpoint we are stopped on, as file + line, or (null, 0) for a break that did
    /// not come from one. Gated on LastBreakReason: BreakpointLastHit keeps naming the last one hit
    /// after the program has moved on, so without the check a later step would report the line of a
    /// breakpoint it left behind.</summary>
    private static (string file, int line) BreakpointLocation()
    {
        try
        {
            var dbg = GetDebugger();
            if (dbg == null || dbg.CurrentMode != dbgDebugMode.dbgBreakMode) { return (null, 0); }
            if (dbg.LastBreakReason != dbgEventReason.dbgEventReasonBreakpoint) { return (null, 0); }

            var bp = dbg.BreakpointLastHit;
            return bp == null || bp.FileLine < 1 ? (null, 0) : (bp.File, bp.FileLine);
        }
        catch (Exception)
        {
            // A conditional or function breakpoint can refuse to answer for a file it never bound
            // to. The caller falls back to the document, which is where this started.
            return (null, 0);
        }
    }

    private static string SafeModule(StackFrame sf)
    {
        try { return sf.Module; } catch { return null; }
    }

    /// <summary>The debugger renders string values wrapped in quotes (e.g. "boom"); strip a single
    /// surrounding pair so the model gets the bare message.</summary>
    private static string Unquote(string s)
    {
        return s?.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
    }
}

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

    /// <summary>The debugger, noting on the way which thread a break landed on. Every entry point
    /// goes through here, which is the only reason the note can be trusted: it is taken before any
    /// of them acts, so it predates the debug_select_thread that would otherwise overwrite it.</summary>
    private static Debugger GetDebugger()
    {
        var dbg = GetDte()?.Debugger;
        if (dbg == null) { return null; }
        try
        {
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode) { _stoppedThreadId = 0; }
            // Only the first read while stopped counts: after that CurrentThread is whatever the
            // caller selected, and taking it again would record the choice instead of the break.
            else if (_stoppedThreadId == 0) { _stoppedThreadId = SafeThreadId(dbg.CurrentThread); }
        }
        catch (Exception) { /* mode unreadable mid-transition: leave the note as it was */ }
        return dbg;
    }

    private static int _stoppedThreadId;

    /// <summary>Forget which thread the break landed on, before resuming. Called by whatever lets
    /// the program run — the next break may be on another thread and can get there before anything
    /// reads the debugger again, which would leave the note naming the thread we left.</summary>
    private static void LeavingBreak() => _stoppedThreadId = 0;

    /// <summary>Reported instead of a real mode by the transitions that do not block — start, stop
    /// and detach all return before VS has moved. A null mode there reads as "something went
    /// wrong"; this says the transition is in flight and debug_get_state has the answer.</summary>
    private const string PendingMode = "pending";

    private static string ModeToString(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design",
        dbgDebugMode.dbgRunMode => "run",
        dbgDebugMode.dbgBreakMode => "break",
        _ => "unknown",
    };

    /// <summary>Where execution is paused: file + 1-based line, from the TOP frame of the stopped
    /// thread — not the selected one, which debug_select_frame moves to inspect a caller. Those are
    /// two different questions: this one is where the program stopped, and it does not change
    /// because someone is reading a caller's locals.
    /// <para>The caret is the fallback, for frames that carry no position of their own. It is not
    /// the first choice because run_to_line and set_next_statement move it themselves (the VS
    /// commands work off the selection), so after either of those it named the line we had just put
    /// there — measured: a break on line 19 reported as line 17, a comment.</para></summary>
    private static (string file, int line) CurrentLocation()
    {
        var fromFrame = FrameLocation(TopFrame());
        if (fromFrame.line > 0) { return fromFrame; }

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

    /// <summary>Frame 0 of the thread the break landed on: where execution actually is.
    /// <para>Neither selection can be trusted for this. Debugger.CurrentStackFrame follows
    /// debug_select_frame, and Debugger.CurrentThread follows debug_select_thread — both move while
    /// the caller reads a caller's locals or another thread's stack, and neither should change the
    /// answer to "where did the program stop". VS exposes no "thread that broke", so the id is
    /// recorded on the way in; see <see cref="StoppedThread"/>. StackFrames is 1-based.</para></summary>
    private static StackFrame TopFrame()
    {
        try
        {
            var frames = StoppedThread()?.StackFrames;
            return frames == null || frames.Count < 1 ? null : frames.Item(1);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The thread the break landed on, found by the id noted when the break arrived.
    /// Falls back to the current thread when the note is missing or the thread is gone.</summary>
    private static EnvDTE.Thread StoppedThread()
    {
        var dbg = GetDebugger();
        if (dbg == null) { return null; }
        if (_stoppedThreadId == 0) { return dbg.CurrentThread; }
        try
        {
            foreach (EnvDTE.Thread t in dbg.CurrentProgram?.Threads ?? (Threads)null)
            {
                if (t.ID == _stoppedThreadId) { return t; }
            }
        }
        catch (Exception) { /* the program went away mid-read */ }
        return dbg.CurrentThread;
    }

    /// <summary>File + 1-based line of one stack frame, or (null, 0) when the frame cannot say.
    /// EnvDTE's own StackFrame carries neither; EnvDTE90a.StackFrame2 adds them. Matched with `is`
    /// rather than cast: a frame with no source (native, mixed-mode) does not implement it, and that
    /// is a fallback case, not an error.</summary>
    private static (string file, int line) FrameLocation(StackFrame frame)
    {
        try
        {
            if (frame is not EnvDTE90a.StackFrame2 sf2) { return (null, 0); }
            var line = (int)sf2.LineNumber;
            return line < 1 ? (null, 0) : (sf2.FileName, line);
        }
        catch (Exception)
        {
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

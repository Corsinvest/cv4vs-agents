/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// <para>
/// Single source of truth for "what does Visual Studio know right now?".
/// Wraps DTE / IVsSolution / IVsDifferenceService / IVsErrorList behind
/// a small, async, thread-safe API. Used by:
///   • the MCP server (Mcp/Tools/*) to answer Claude CLI tool calls
///   • the WebView chat (Host/WebViewMessageHandler) to inject context
///     into prompts
///   • the live "editor context" indicator (selection / file badge)
/// </para>
/// <para>
/// All members marshal to the UI thread internally — callers can be on
/// any thread. Read-only operations only; mutations (open file, diff,
/// close tab) are explicit.
/// </para>
/// </summary>
internal sealed partial class IdeContextService : IDisposable
{
    private static readonly Lazy<IdeContextService> _instance = new(() => new IdeContextService());
    public static IdeContextService Instance => _instance.Value;

    private IdeContextService() { }

    // Live selection tracking. The view's own events are the only reliable signal for editor
    // selection/caret — DTE doesn't fire on mouse selection. IVsMonitorSelection tells us which
    // view owns the context when the active frame changes.

    private IVsEditorAdaptersFactoryService _editorAdapters;
    private ITextDocumentFactoryService _docFactory;
    private IVsMonitorSelection _monitorSelection;
    private uint _selectionCookie;
    private bool _subscribed;

    // The view that currently owns the context. Its own events drive the emit; nothing
    // re-attaches, so there is no "the tracker missed it" state to recover from.
    private EditorSelectionState _active;

    // SelectionChanged fires rapidly while dragging; coalesce to one emit per 150ms. The
    // bridge and the MCP broadcast are the reason this stays — unlike a local adornment,
    // each emit crosses a process boundary.
    private Timer _debounce;
    private EditorContext _pending;

    // Last-emitted state — suppress duplicate notifications when nothing the badge or the
    // CLI cares about actually changed.
    private string _lastFilePath;
    private bool _lastHasSelection;
    private int _lastStartLine;
    private int _lastEndLine;
    private int _lastStartCol;
    private int _lastEndCol;
    private bool _hasEmitted;

    /// <summary>Fires whenever the active editor file or its selection
    /// changes. <c>null</c> argument = no document active.</summary>
    public event Action<EditorContext> ContextChanged;

    /// <summary>Start tracking the active editor's selection. Idempotent.
    /// Must be called on the UI thread. The MEF
    /// <see cref="IVsEditorAdaptersFactoryService"/> is resolved via
    /// IComponentModel so we can map IVsTextView → IWpfTextView.</summary>
    public void SubscribeToEditorEvents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_subscribed) { return; }
        try
        {
            var components = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            _editorAdapters = components?.GetService<IVsEditorAdaptersFactoryService>();
            _docFactory = components?.GetService<ITextDocumentFactoryService>();
            if (_editorAdapters == null || _docFactory == null)
            {
                OutputWindowLogger.Global.Warn("[ide-context] editor services unavailable — selection tracking will not fire");
            }
            else
            {
                OutputWindowLogger.Global.Info("[ide-context] editor-events subscribed");
            }
            _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

            // Hear about active-frame changes so we re-attach to the new view.
            _monitorSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            _monitorSelection?.AdviseSelectionEvents(new SelectionEventSink(this), out _selectionCookie);

            _subscribed = true;
            TrackActiveView();
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.SubscribeToEditorEvents", ex); }
    }

    /// <summary>Point the context at the active text view, attaching per-view tracking the first
    /// time we meet it. When <paramref name="frame"/> is given we read the view straight from it
    /// (avoids IVsTextManager.GetActiveView timing gaps).</summary>
    internal void TrackActiveView(IVsWindowFrame frame = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            IVsTextView vsView = null;
            if (frame != null)
            {
                vsView = VsShellUtilities.GetTextView(frame);
            }
            else
            {
                var tm = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
                tm?.GetActiveView(0, null, out vsView);
            }

            var wpf = vsView != null ? _editorAdapters?.GetWpfTextView(vsView) : null;
            if (wpf == null)
            {
                // Not a text editor. A non-editor frame taking focus must not clear the context —
                // only the last document closing does, and that arrives as the view's Closed event.
                return;
            }

            // Only a real document editor counts. Output / Find-results / readonly tool windows are
            // IWpfTextViews too, but their role is not Document.
            if (!wpf.Roles.Contains(PredefinedTextViewRoles.Document)) { return; }

            var state = EditorSelectionState.GetOrCreate(wpf, _docFactory, OnTrackedViewChanged);
            if (state == null) { return; }

            // The outgoing state is deliberately not detached: its view is still open and may be
            // activated again, so it must keep listening; it dies with the view anyway.
            _active = state;
            CaptureAndSchedule();
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.TrackActiveView", ex); }
    }

    /// <summary>A tracked view reported a selection, focus or close change.
    /// <para>Every open view keeps listening, so the firing one has to be identified: a selection
    /// in a side-by-side document the user has not activated is not the context, and closing a
    /// background tab must not clear or rebuild the active one.</para></summary>
    private void OnTrackedViewChanged(EditorSelectionState state)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!ReferenceEquals(state, _active)) { return; }

        if (state.View.IsClosed)
        {
            _active = null;
            ScheduleEmit(null);
            return;
        }
        CaptureAndSchedule();
    }

    /// <summary>Snapshot the tracked view (UI thread) and schedule a debounced emit.</summary>
    private void CaptureAndSchedule()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // No consumer (MCP server down at 0 sessions, no chat pane hooked) → skip the work. The
        // sink stays advised; only its downstream work is gated. A fresh client still pulls the
        // current context via McpServerHost.DelayedSendInitialContextAsync.
        if (ContextChanged == null) { return; }
        ScheduleEmit(BuildContext(_active, includeText: true));
    }

    /// <summary>The one projection from tracked state to <see cref="EditorContext"/>. Both the
    /// push path and the on-demand readers go through here, so no two of them can disagree about
    /// lines, columns or emptiness.
    /// <para><paramref name="includeText"/> false leaves <see cref="EditorContext.SelectedText"/>
    /// empty: the badge never reads it, and materialising a multi-megabyte selection for a
    /// consumer that wants two integers is the allocation this exists to avoid.</para></summary>
    /// <summary>The span's own answer to <see cref="SelectionGeometry.IsEffectivelyEmpty"/>: no
    /// characters, or nothing but whitespace. Reads the snapshot position by position and stops at
    /// the first real character, so a large selection costs one character rather than its length.</summary>
    private static bool IsSpanEffectivelyEmpty(SnapshotSpan span)
    {
        var start = span.Start.Position;
        var snapshot = span.Snapshot;
        return SelectionGeometry.IsEffectivelyEmpty(span.Length, i => snapshot[start + i]);
    }

    private EditorContext BuildContext(EditorSelectionState state, bool includeText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (state == null || state.View.IsClosed) { return null; }
        try
        {
            var filePath = state.Document?.FilePath;
            // Must be a real file on disk: a Document view can still wrap a synthetic path
            // ("\temp\readonly\Grep output").
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                OutputWindowLogger.Global.Trace(() => $"[ide-context] drop: path missing/synthetic='{filePath}'");
                return null;
            }

            if (!state.TryGetSpan(out var span, out _)) { return null; }

            // The debounce leaves a window in which the buffer can change — the user typing, or
            // Claude editing the file — so the captured span may belong to an older version.
            // TranslateTo moves it forward on the same buffer, which is what this is: both sides
            // are the view's top-level buffer, only the version differs.
            var snapshot = state.View.TextSnapshot;
            if (span.Snapshot != snapshot)
            {
                span = span.TranslateTo(snapshot, SpanTrackingMode.EdgeExclusive);
            }

            // Asked over the span rather than over its text: the caller that only wants to know
            // whether there IS a selection (the context menu, on every query VS raises) must not
            // pay for materialising a five-thousand-line one.
            var isEmpty = IsSpanEffectivelyEmpty(span);
            var text = !includeText || isEmpty ? string.Empty : span.GetText();

            var startLine = snapshot.GetLineFromPosition(span.Start.Position);
            var endLine = snapshot.GetLineFromPosition(span.End.Position);
            var starts = new int[endLine.LineNumber - startLine.LineNumber + 1];
            var ends = new int[starts.Length];
            for (var i = 0; i < starts.Length; i++)
            {
                var line = snapshot.GetLineFromLineNumber(startLine.LineNumber + i);
                starts[i] = line.Start.Position;
                ends[i] = line.End.Position;
            }
            var geo = SelectionGeometry.Compute(span.Start.Position, span.End.Position, starts, ends);

            return new EditorContext
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                HasSelection = !isEmpty,
                StartLine = startLine.LineNumber + geo.StartLine,
                EndLine = startLine.LineNumber + geo.EndLine,
                StartColumn = geo.StartCol,
                EndColumn = geo.EndCol,
                SelectedText = includeText && !isEmpty ? text : string.Empty,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.BuildContext", ex);
            return null;
        }
    }

    private void ScheduleEmit(EditorContext ctx)
    {
        // Defence in depth for the paths that reach here without CaptureAndSchedule
        // (OnTrackedViewChanged's clear): don't arm the debounce with no consumer.
        if (ContextChanged == null) { return; }
        _pending = ctx;
        try { _debounce?.Change(150, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnDebounceElapsed()
    {
        var ctx = _pending;
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Emit(ctx);
        }).FileAndForget("cv4vs/Ide.OnDebounceElapsed");
    }

    private void Emit(EditorContext ctx)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ctx == null)
        {
            if (_hasEmitted && _lastFilePath != null)
            {
                _lastFilePath = null;
                _lastHasSelection = false;
                _lastStartLine = _lastEndLine = _lastStartCol = _lastEndCol = 0;
                ContextChanged?.Invoke(null);
            }
            _hasEmitted = true;
            return;
        }
        // Skip caret-only moves: without a selection the caret position isn't context Claude cares
        // about (it wants the SELECTION), so moving the cursor in the same file must not re-emit.
        // When neither side has a selection, dedup on FilePath alone; the position fields are compared
        // only when a selection is involved (so a real selection change, and the select↔deselect
        // transition, still emit).
        // The columns are part of that comparison because the CLI keys on them: selecting one word
        // and then another on the SAME line changes nothing else, and dropping the emit would leave
        // the model holding the first word.
        if (_hasEmitted &&
            ctx.FilePath == _lastFilePath &&
            ctx.HasSelection == _lastHasSelection &&
            (!ctx.HasSelection ||
             (ctx.StartLine == _lastStartLine &&
              ctx.EndLine == _lastEndLine &&
              ctx.StartColumn == _lastStartCol &&
              ctx.EndColumn == _lastEndCol)))
        {
            return;
        }
        _lastFilePath = ctx.FilePath;
        _lastHasSelection = ctx.HasSelection;
        _lastStartLine = ctx.StartLine;
        _lastEndLine = ctx.EndLine;
        _lastStartCol = ctx.StartColumn;
        _lastEndCol = ctx.EndColumn;
        _hasEmitted = true;
        // Keep the MCP latest-selection cache warm so the CLI can still grab
        // "the last thing I selected" after focus moves to the chat/CLI pane.
        if (ctx.HasSelection) { RememberSelection(ctx.ToSelection()); }

        try { ContextChanged?.Invoke(ctx); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.ContextChanged", ex); }
    }

    /// <summary>Synchronous snapshot of the editor for the on-demand readers (badge refresh, the
    /// CLI's initial context, the `&lt;ide_selection&gt;` block, the context menu). Reads the same
    /// tracked state the push path emits from, so a pull and a push can never disagree. Must be
    /// called on the UI thread.</summary>
    public EditorContext GetCurrentContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // No usable state: either no view has been met yet, or the one we hold has been closed
        // under us — closing a solution takes its editors with it, and the close event for a view
        // that was not the active one leaves the field pointing at a dead view. Drop it first, so
        // that finding no replacement leaves nothing rather than the corpse.
        if (_active?.View.IsClosed == true) { _active = null; }
        if (_active == null) { TrackActiveView(); }
        return BuildContext(_active, includeText: true);
    }

    /// <summary>Whether the active editor has a selection worth reporting, without building the
    /// context to find out. The editor context menu asks this from OnBeforeQueryStatus, which VS
    /// raises continuously and once per entry — going through GetCurrentContext there would
    /// materialise the selected text on every keystroke's worth of menu state.</summary>
    public bool HasSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_active?.View.IsClosed == true) { _active = null; }
        if (_active == null) { TrackActiveView(); }
        return BuildContext(_active, includeText: false)?.HasSelection == true;
    }

    /// <summary>If <paramref name="filePath"/> is open in an editor with unsaved changes, save it.
    /// Used by the autosave hook so Claude reads/writes the live editor content, not the stale
    /// on-disk version — so the caller must AWAIT this before letting the tool run, or the save
    /// races the read it exists to precede. Safe to call from any thread (marshals to the UI
    /// thread); no-op if the file isn't open or clean. False means the file is open, dirty, and
    /// could NOT be saved: whatever reads it next gets a stale version.</summary>
    public async Task<bool> SaveIfDirtyAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return true; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte?.Documents == null) { return true; }
            var target = PathHelpers.FromFileUri(filePath);
            foreach (Document doc in dte.Documents)
            {
                if (string.Equals(doc.FullName, target, StringComparison.OrdinalIgnoreCase))
                {
                    if (!doc.Saved) { doc.Save(); }
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            // Read-only file, denied permissions, an editor refusing to save: the caller must
            // tell Claude, or it reads a stale file believing it is current.
            OutputWindowLogger.Global.LogException("Ide.SaveIfDirty", ex);
            return false;
        }
        // Not open in any editor — nothing to save, and nothing stale either.
        return true;
    }

    /// <summary>Save the given file if it's open and dirty. Returns true if a save
    /// happened, false if the file wasn't open or was already saved.</summary>
    public async Task<bool> SaveDocumentAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = PathHelpers.FromFileUri(filePath);
            foreach (var frame in DocumentFrames())
            {
                if (PathEquals(FrameMoniker(frame), target))
                {
                    if (!FrameDirty(frame)) { return false; }
                    // Save via the frame's doc-data (works for preview tabs too).
                    if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var o) == VSConstants.S_OK
                        && o is IVsPersistDocData pdd)
                    {
                        pdd.SaveDocData(VSSAVEFLAGS.VSSAVE_Save, out _, out _);
                        return true;
                    }
                    return false;
                }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.SaveDocumentAsync", ex); }
        return false;
    }

    /// <summary>True if the file is open in the IDE with unsaved changes. Null when
    /// the file isn't open in any editor (so the caller can distinguish
    /// "clean" from "not open"). For the MCP checkDocumentDirty tool.</summary>
    public async Task<bool?> IsDocumentDirtyAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return null; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = PathHelpers.FromFileUri(filePath);
            foreach (var frame in DocumentFrames())
            {
                if (PathEquals(FrameMoniker(frame), target)) { return FrameDirty(frame); }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.IsDocumentDirtyAsync", ex); }
        return null;
    }

    public sealed class BufferReadResult
    {
        public bool Ok { get; set; }
        public string Path { get; set; }
        public bool IsDirty { get; set; }
        public string Content { get; set; }
        public int TotalLines { get; set; }
        public bool Truncated { get; set; }

        /// <summary>1-based line the content starts at, so a range read can be placed back in the
        /// file without counting from the top.</summary>
        public int StartLine { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Read an open document's editor buffer — the text as it is on screen, unsaved
    /// changes included. With no path, reads the active document: "what I'm looking at" is the
    /// gesture this exists for, and it's the user who picks it, not the model. The on-disk
    /// version is the Read tool's job; this one is for what hasn't been written yet.</summary>
    public async Task<BufferReadResult> ReadDocumentBufferAsync(string filePath, int maxLines, int startLine, int endLine)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
            if (dte == null) { return new BufferReadResult { Reason = "DTE not available." }; }

            Document doc;
            if (string.IsNullOrEmpty(filePath))
            {
                doc = dte.ActiveDocument;
                if (doc == null) { return new BufferReadResult { Reason = "No active document." }; }
            }
            else
            {
                doc = null;
                var target = PathHelpers.FromFileUri(filePath);
                // Ask the shell whether it is open, then DTE for the buffer: the frames see preview
                // tabs that DTE.Documents does not, and the moniker they answer with is a path DTE
                // will match on.
                var moniker = OpenDocumentMoniker(target);
                if (moniker != null)
                {
                    foreach (Document d in dte.Documents)
                    {
                        if (PathEquals(d.FullName, moniker)) { doc = d; break; }
                    }
                }
                if (doc == null)
                {
                    // Not open means there is no buffer — the file on disk is Read's job.
                    return new BufferReadResult { Reason = $"'{target}' is not open in an editor; use the Read tool for the on-disk version." };
                }
            }

            if (doc.Object("TextDocument") is not TextDocument td)
            {
                // Designers, binary editors: a document window without a text buffer.
                return new BufferReadResult { Reason = $"'{doc.FullName}' has no text buffer (not a text editor)." };
            }

            var totalLines = td.EndPoint.Line;

            // A range wins over maxLines: asked for lines 400-450, taking 450 from the top and
            // throwing away 400 is the whole file's worth of tokens for fifty lines of answer.
            var from = startLine > 0 ? Math.Min(startLine, totalLines) : 1;
            var to = endLine > 0 ? Math.Min(Math.Max(endLine, from), totalLines) : 0;

            var start = td.StartPoint.CreateEditPoint();
            if (from > 1) { start.MoveToLineAndOffset(from, 1); }

            bool truncated;
            string text;
            if (to > 0)
            {
                var stop = td.StartPoint.CreateEditPoint();
                // Start of the line after the last one wanted, so the range is inclusive.
                if (to < totalLines) { stop.MoveToLineAndOffset(to + 1, 1); }
                else { stop.MoveToPoint(td.EndPoint); }
                text = start.GetText(stop) ?? string.Empty;
                // Not "there is more file after this": the caller asked for a range and got all of
                // it, so nothing was cut. Saying otherwise made truncated useless for deciding
                // whether to ask again — which is the only thing it is for. It only turns true when
                // the range itself was clipped, i.e. endLine ran past the end of the file.
                truncated = endLine > totalLines;
            }
            else if (maxLines > 0 && totalLines - from + 1 > maxLines)
            {
                // Keep the head: unlike an output pane, a file is read top-down.
                var stop = td.StartPoint.CreateEditPoint();
                stop.MoveToLineAndOffset(from + maxLines, 1);
                text = start.GetText(stop) ?? string.Empty;
                truncated = true;
            }
            else
            {
                text = start.GetText(td.EndPoint) ?? string.Empty;
                truncated = false;
            }

            return new BufferReadResult
            {
                Ok = true,
                Path = doc.FullName,
                IsDirty = !doc.Saved,
                Content = text,
                TotalLines = totalLines,
                Truncated = truncated,
                StartLine = from,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.ReadDocumentBufferAsync", ex);
            return new BufferReadResult { Reason = $"Read error: {ex.Message}" };
        }
    }

    public void Dispose()
    {
        try
        {
            _active?.Detach();
            _active = null;
            if (_monitorSelection != null && _selectionCookie != 0)
            {
                _monitorSelection.UnadviseSelectionEvents(_selectionCookie);
                _selectionCookie = 0;
            }
            _debounce?.Dispose();
        }
        catch { /* silent: cleanup */ }
        _monitorSelection = null;
        _editorAdapters = null;
        _docFactory = null;
        _debounce = null;
        _subscribed = false;
    }

    //  Latest-selection cache (for MCP getLatestSelection)

    private EditorSelection _latestSelection;

    internal void RememberSelection(EditorSelection sel)
    {
        if (sel?.IsEmpty == false) { _latestSelection = sel; }
    }

    public EditorSelection GetLatestSelection() => _latestSelection;

    /// <summary>Emit the current context even though nothing changed, for a consumer that has just
    /// arrived and holds nothing: a chat pane whose WebView is only now able to receive, or the
    /// IDE-context eye being reopened. Both subscribe to <see cref="ContextChanged"/> and would
    /// otherwise wait for the next editor event to learn what is open.
    /// <para>The dedup is what it defeats — the tracker itself never needs waking.</para></summary>
    public void ResendCurrentContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _hasEmitted = false;
        _lastFilePath = null;
        Emit(GetCurrentContext());
    }


    /// <summary>IVsMonitorSelection sink: when the active window frame changes
    /// we re-attach the SelectionChanged listener to the new view's editor.
    /// Only the frame-change element interests us.</summary>
    private sealed class SelectionEventSink(IdeContextService owner) : IVsSelectionEvents
    {
        int IVsSelectionEvents.OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
            {
                owner.TrackActiveView(varValueNew as IVsWindowFrame);
            }
            return VSConstants.S_OK;
        }

        int IVsSelectionEvents.OnSelectionChanged(
            IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
            IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
            => VSConstants.S_OK;

        int IVsSelectionEvents.OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;
    }
}

/// <summary>Snapshot of the active editor state for the live badge.</summary>
internal sealed class EditorContext
{
    public string FilePath { get; set; }
    public string FileName { get; set; }
    public bool HasSelection { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int StartColumn { get; set; }
    public int EndColumn { get; set; }
    public string SelectedText { get; set; }

    /// <summary>Project this badge snapshot to the MCP-facing
    /// <see cref="EditorSelection"/> shape (used to keep the
    /// latest-selection cache warm).</summary>
    public EditorSelection ToSelection() => new()
    {
        FilePath = FilePath,
        Text = SelectedText ?? string.Empty,
        StartLine = StartLine,
        StartColumn = StartColumn,
        EndLine = EndLine,
        EndColumn = EndColumn,
        IsEmpty = !HasSelection,
    };
}

internal sealed class EditorSelection
{
    public string FilePath { get; set; }
    public string Text { get; set; }
    public int StartLine { get; set; }
    public int StartColumn { get; set; }
    public int EndLine { get; set; }
    public int EndColumn { get; set; }
    public bool IsEmpty { get; set; }
}

internal sealed class OpenEditor
{
    public string FilePath { get; set; }
    public bool IsActive { get; set; }
    public bool IsDirty { get; set; }
    public string Language { get; set; }
}

internal sealed class Diagnostic
{
    public string Message { get; set; }
    public string Severity { get; set; }
    public DiagnosticRange Range { get; set; }
    public string Source { get; set; }
    public string Code { get; set; }
}

internal sealed class DiagnosticRange
{
    public DiagnosticPosition Start { get; set; }
    public DiagnosticPosition End { get; set; }
}

internal sealed class DiagnosticPosition
{
    public int Line { get; set; }
    public int Character { get; set; }
}

internal sealed class DiagnosticFile
{
    public string Uri { get; set; }
    public List<Diagnostic> Diagnostics { get; set; }
}

internal sealed class BuildResult
{
    public bool Ok { get; set; }
    public int FailedProjects { get; set; }
    public string Message { get; set; }

    /// <summary>Solution configuration the build ran under ("Debug|Any CPU"), so the caller can
    /// tell which one it got — it is the IDE's active one, which the caller did not choose.</summary>
    public string Configuration { get; set; }

    /// <summary>Errors, plus warnings and info when the caller asked for them — one list, each
    /// entry saying what it is, the way ide_get_diagnostics reports the same Error List.</summary>
    public List<BuildError> Errors { get; set; } = [];
}

internal sealed class BuildError
{
    public string File { get; set; }
    public int Line { get; set; }
    public string Description { get; set; }
    public string Project { get; set; }

    /// <summary>"Error", "Warning" or "Info" — an entry is no longer necessarily an error.</summary>
    public string Severity { get; set; }
}

internal sealed class SolutionStructure
{
    public string SolutionPath { get; set; }
    public List<ProjectNode> Projects { get; set; }
}

internal sealed class ProjectNode
{
    public string Name { get; set; }
    public string Path { get; set; }
    public List<string> Files { get; set; }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using EnvDTE80;
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

    //  Live selection tracking (real editor, not DTE)
    //
    // IWpfTextView.Selection.SelectionChanged is the only reliable signal for
    // editor selection/caret (the DTE path didn't fire on mouse selection).
    // IVsMonitorSelection tells us when the active frame changes so we re-attach.

    private IVsEditorAdaptersFactoryService _editorAdapters;
    private IVsMonitorSelection _monitorSelection;
    private uint _selectionCookie;
    private IWpfTextView _trackedView;
    private bool _subscribed;

    /// <summary>Raised (UI thread) when VS's active window frame changes. Panes use it to blur
    /// their input when they are no longer the active frame — the WebView2 gets no DOM blur across
    /// the HwndHost boundary, so the caret would keep blinking otherwise.</summary>
    internal static event Action ActiveFrameChanged;

    // Debounced push: SelectionChanged can fire rapidly while dragging; coalesce
    // to one emit per 150ms. The dedup below drops no-op re-emits on top.
    private Timer _debounce;
    private EditorContext _pending;

    // Last-emitted state — suppress duplicate notifications when nothing the
    // badge/CLI cares about actually changed.
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
    /// Must be called on the UI thread. The package passes in the MEF
    /// <see cref="IVsEditorAdaptersFactoryService"/> (resolved via
    /// IComponentModel) so we can map IVsTextView → IWpfTextView.</summary>
    public void SubscribeToEditorEvents(IVsEditorAdaptersFactoryService editorAdapters = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_subscribed) { return; }
        try
        {
            _editorAdapters = editorAdapters
                ?? (Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel)
                    ?.GetService<IVsEditorAdaptersFactoryService>();
            if (_editorAdapters == null)
            {
                OutputWindowLogger.Warn("[ide-context] editor adapters unavailable — selection tracking will not fire");
            }
            else
            {
                OutputWindowLogger.Info("[ide-context] editor-events subscribed");
            }
            _debounce = new Timer(_ => OnDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

            // Hear about active-frame changes so we re-attach to the new view.
            _monitorSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
            _monitorSelection?.AdviseSelectionEvents(new SelectionEventSink(this), out _selectionCookie);

            _subscribed = true;
            TrackActiveView();
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.SubscribeToEditorEvents", ex); }
    }

    /// <summary>(Re)attach the SelectionChanged listener to the active text
    /// view. When <paramref name="frame"/> is given we read the view straight
    /// from it (avoids IVsTextManager.GetActiveView timing gaps).</summary>
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
                // Not a text editor. Ask the text manager for ANY active view to
                // disambiguate: none → all editors closed, clear stale context;
                // some → a non-editor frame got focus, keep current context.
                // (IWpfTextView.Closed alone is unreliable for the tracked view.)
                IVsTextView anyView = null;
                (Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager)
                    ?.GetActiveView(0, null, out anyView);
                if (anyView == null)
                {
                    UntrackView();
                    ScheduleEmit(null);
                }
                return;
            }
            if (wpf == _trackedView) { return; }

            UntrackView();
            _trackedView = wpf;
            _trackedView.Selection.SelectionChanged += OnViewSelectionChanged;
            _trackedView.Closed += OnViewClosed;
            CaptureAndSchedule();
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.TrackActiveView", ex); }
    }

    private void UntrackView()
    {
        if (_trackedView == null) { return; }
        try
        {
            _trackedView.Selection.SelectionChanged -= OnViewSelectionChanged;
            _trackedView.Closed -= OnViewClosed;
        }
        catch { /* view already torn down */ }
        _trackedView = null;
    }

    private void OnViewSelectionChanged(object sender, EventArgs e) => CaptureAndSchedule();
    private void OnViewClosed(object sender, EventArgs e)
    {
        UntrackView();
        // No active text view anymore → clear context (badge/CLI drop it).
        ScheduleEmit(null);
    }

    /// <summary>Snapshot the current selection (UI thread) into an
    /// <see cref="EditorContext"/> and schedule a debounced emit.</summary>
    private void CaptureAndSchedule()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // No consumer (MCP server down at 0 sessions, no chat pane hooked) → skip the
        // snapshot/GetText work. The sink stays advised (MS pattern); only its downstream
        // work is gated. Push-emit is dead here, but a fresh client still pulls the current
        // context via McpServerHost.DelayedSendInitialContextAsync (on-demand GetCurrentContext).
        // Trade-off: _latestSelection (getLatestSelection cache) isn't kept warm while gated,
        // so a selection made with the extension closed is lost; it repopulates on the next
        // TrackActiveView when a session reopens. Acceptable — nobody reads it until then.
        if (ContextChanged == null) { return; }
        var view = _trackedView;
        if (view == null) { ScheduleEmit(null); return; }
        try
        {
            // Only a real document editor counts. Output / Find-results / readonly
            // tool windows are also IWpfTextViews, but their view role is not
            // Document — selecting there must NOT become IDE context.
            if (!view.Roles.Contains(PredefinedTextViewRoles.Document))
            {
                ScheduleEmit(null);
                return;
            }

            var sel = view.Selection;
            var snapshot = view.TextSnapshot;

            string filePath = null;
            if (view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
            {
                filePath = doc?.FilePath;
            }
            // And it must be a real file on disk (a Document view can still wrap a
            // synthetic path like "\temp\readonly\Grep output" / "Temp.txt").
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                OutputWindowLogger.Trace(() => $"[ide-context] drop: path missing/synthetic='{filePath}'");
                ScheduleEmit(null);
                return;
            }

            var isEmpty = sel.IsEmpty;
            var startLine = snapshot.GetLineFromPosition(sel.Start.Position.Position);
            var endLine = snapshot.GetLineFromPosition(sel.End.Position.Position);
            var ctx = new EditorContext
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                HasSelection = !isEmpty,
                // VS editor lines are 0-based; we keep 1-based to match the
                // rest of the code (DTE used 1-based) — MCP subtracts 1.
                StartLine = startLine.LineNumber + 1,
                EndLine = endLine.LineNumber + 1,
                StartColumn = sel.Start.Position.Position - startLine.Start.Position,
                EndColumn = sel.End.Position.Position - endLine.Start.Position,
                SelectedText = isEmpty
                    ? string.Empty
                    : snapshot.GetText(sel.Start.Position.Position, sel.End.Position.Position - sel.Start.Position.Position),
            };
            ScheduleEmit(ctx);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.CaptureAndSchedule", ex); }
    }

    private void ScheduleEmit(EditorContext ctx)
    {
        // Defence in depth for the paths that reach here without CaptureAndSchedule
        // (OnViewClosed, TrackActiveView's clear): don't arm the debounce with no consumer.
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
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.ContextChanged", ex); }
    }

    /// <summary>Synchronous snapshot of the editor for the live badge
    /// path. Must be called on the UI thread.</summary>
    public EditorContext GetCurrentContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            var doc = dte?.ActiveDocument;
            var path = doc?.FullName;
            // Only real code files; skip output/readonly/tool windows (synthetic path).
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return null; }
            var ctx = new EditorContext
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
            };
            // Non-text docs (designers, images, .resx) expose a TextDocument
            // proxy whose Selection getter throws COMException E_FAIL — expected,
            // so swallow it silently instead of spamming the Output window.
            try
            {
                if (doc.Object("TextDocument") is TextDocument textDoc &&
                    textDoc.Selection is TextSelection sel)
                {
                    if (!sel.IsEmpty)
                    {
                        ctx.HasSelection = true;
                        ctx.StartLine = sel.TopLine;
                        ctx.EndLine = sel.BottomLine;
                        ctx.StartColumn = Math.Max(0, (sel.TopPoint?.DisplayColumn ?? 1) - 1);
                        ctx.EndColumn = Math.Max(0, (sel.BottomPoint?.DisplayColumn ?? 1) - 1);
                        ctx.SelectedText = sel.Text;
                    }
                    else
                    {
                        ctx.StartLine = sel.CurrentLine;
                        ctx.EndLine = sel.CurrentLine;
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException) { /* designer/non-text doc — no selection */ }
            return ctx;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Ide.GetCurrentContext", ex);
            return null;
        }
    }

    /// <summary>If <paramref name="filePath"/> is open in an editor with unsaved
    /// changes, save it. Used by the autosave hook so Claude reads/writes the
    /// live editor content, not the stale on-disk version. Safe to call from any
    /// thread (marshals to the UI thread); no-op if the file isn't open or clean.</summary>
    public void SaveIfDirty(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
                if (dte?.Documents == null) { return; }
                var target = PathHelpers.FromFileUri(filePath);
                foreach (Document doc in dte.Documents)
                {
                    if (string.Equals(doc.FullName, target, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!doc.Saved) { doc.Save(); }
                        return;
                    }
                }
            }
            catch (Exception ex) { OutputWindowLogger.LogException("Ide.SaveIfDirty", ex); }
        }).FileAndForget("cv4vs/Ide.SaveIfDirty");
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
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.SaveDocumentAsync", ex); }
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
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.IsDocumentDirtyAsync", ex); }
        return null;
    }


    public void Dispose()
    {
        try
        {
            UntrackView();
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

    /// <summary>Force an emit of the current editor context, bypassing
    /// the dedup-by-state filter. Used by the package after a solution
    /// finishes opening: VS may have restored the previous active file +
    /// selection from persisted state without firing a SelectionChanged we
    /// listen to, so MCP clients (CLI / chat) would otherwise have no idea
    /// what's currently open. Also (re)attaches the view tracker.</summary>
    public void ForceEmitCurrentContext()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Reset the dedup baseline so the emit fires even if unchanged.
        _hasEmitted = false;
        _lastFilePath = null;
        TrackActiveView();          // re-attach to whatever view is active now
        Emit(GetCurrentContext());  // sync snapshot via DTE (on-demand path)
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
                ActiveFrameChanged?.Invoke();
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

//  DTOs

/// <summary>Snapshot of the active editor state for the live badge
/// (formerly <c>Host.EditorContext</c>).</summary>
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
    public List<BuildError> Errors { get; set; } = [];
}

internal sealed class BuildError
{
    public string File { get; set; }
    public int Line { get; set; }
    public string Description { get; set; }
    public string Project { get; set; }
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// IVsDifferenceCodeWindow2 is provisional ([Obsolete]) in the SDK. The Stage 0 probe found it working on the Visual Studio
// it was tried on, and the review declines a file (and the comparison window takes over) where it does not.
#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>One file's review in its own editor window: the document's code window switched into a difference view
/// against the baseline of its row, until the file has nothing left to review or the window goes.
/// <para>The baseline is shown from a scratch copy that the editor opens (so it is coloured like the document), and
/// every hunk action is a plain edit of one of the two buffers: <b>Keep</b> writes the document's text of the hunk into
/// the baseline's buffer, so the hunk is no longer a difference; <b>Undo</b> writes the baseline's text of the hunk into
/// the document, and saves it. Keep also has to survive the window: the baseline the row keeps is rewritten (see
/// <see cref="SessionChanges.ReplaceBaselineAsync"/>) in the file's own encoding, so that Open, the counts and a later Undo
/// of the whole file agree with what the editor shows.</para>
/// <para>Everything here runs on the UI thread. <see cref="BaselineDocumentRegistry"/> decides which file gets one.</para></summary>
internal sealed class BaselineDocumentController
{
    private const string ViewModeOption = "Diff/View/ViewMode";

    // A hunk action edits a buffer and the difference is recomputed in the background: until it has been, the spans a second
    // action would use are the old ones. The flag is cleared by the recompute, and by this limit if there is none.
    private static readonly TimeSpan PendingLimit = TimeSpan.FromSeconds(2);

    private readonly BaselineDocumentRegistry _registry;
    private readonly OutputWindowLogger _log = OutputWindowLogger.Global;
    private readonly IVsWindowFrame _frame;
    private readonly IVsDifferenceCodeWindow2 _window;
    private readonly IDifferenceTextViewModel _model;
    private readonly ReviewBaseline _baseline;
    private readonly ITextDocument _document;

    private string _scratch;
    private IVsInvisibleEditor _editor;
    private IVsTextLines _lines;
    private ITextBuffer _left;
    private IDifferenceBuffer _differences;
    private IDisposable _adornments;
    private bool _entered;
    private bool _sawDifferences;
    private bool _undoingAll;
    private bool _goToNext;
    private bool _goToFirst;
    private IWpfTextView _navigateIn;
    private DateTime _busyUntilUtc;

    public BaselineDocumentController(BaselineDocumentRegistry registry, SessionChanges owner, FileChange row, IVsWindowFrame frame,
                                      IWpfTextView view, ITextDocument document, IVsDifferenceCodeWindow2 window,
                                      IDifferenceTextViewModel model, ReviewBaseline baseline)
    {
        _registry = registry;
        Owner = owner;
        Path = row.Path;
        RowSerial = row.Serial;
        _frame = frame;
        View = view;
        _document = document;
        _window = window;
        _model = model;
        _baseline = baseline;
    }

    public SessionChanges Owner { get; }
    public string Path { get; }

    /// <summary>Which row this review began with: when the row is replaced (a Keep, then another edit) the review is too.</summary>
    public long RowSerial { get; }

    /// <summary>The document's own view, the one that existed before the window switched.</summary>
    public IWpfTextView View { get; }

    public DateTime EnteredUtc { get; private set; }
    public bool Exited { get; private set; }

    /// <summary>The baseline of the row is being rewritten by this review, so the row is not a new one.</summary>
    public bool IsReplacingBaseline { get; private set; }

    /// <summary>The viewer shared by the window's views, to tell a view that belongs to this review from one that does not.</summary>
    public IWpfDifferenceViewer Viewer => _model.Viewer;

    /// <summary>A hunk action is under way and the difference has not caught up with it.</summary>
    public bool IsBusy => DateTime.UtcNow < _busyUntilUtc;

    /// <summary>The file did not exist before: its one hunk is the whole file, so Keep and Undo of a hunk are those of the file
    /// (an Undo deletes it, as the list's does).</summary>
    public bool IsWholeFile => _baseline.IsEmpty;

    public bool Matches(IWpfTextView view)
        => ReferenceEquals(view, View) || (HunkLocator.TryGetModel(view, out var model) && ReferenceEquals(model.Viewer, _model.Viewer));

    /// <summary>The current difference, or null before the viewer has computed one.</summary>
    public ISnapshotDifference Difference => _differences?.CurrentSnapshotDifference;

    /// <summary>Switch the window into a difference view, in <paramref name="mode"/>. False, with the editor left as it was,
    /// when anything on the way does not work: the registry then declines the file.</summary>
    public bool Enter(DifferenceViewMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // The baseline in the document's line endings: the two sides then differ in lines and not in how they end, and a
            // hunk moved between them brings no foreign line ending along.
            var text = BaselineText.WithLineEnding(_baseline.Text, BaselineText.DominantLineEnding(View.TextSnapshot.GetText()));

            _scratch = Owner.Baselines.TargetFor(Owner.SessionId, Path);
            // UTF-8 with its mark, whatever the file is: this copy is read back by the editor, and with the mark it cannot
            // take accented text for a code page. The bytes the list keeps are written separately, in the file's own encoding.
            File.WriteAllBytes(_scratch, BaselineText.Encode(text, ChangeEvaluator.Mark.Utf8));

            var manager = (IVsInvisibleEditorManager)Package.GetGlobalService(typeof(SVsInvisibleEditorManager));
            ErrorHandler.ThrowOnFailure(manager.RegisterInvisibleEditor(
                _scratch, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out _editor));
            var iid = typeof(IVsTextLines).GUID;
            ErrorHandler.ThrowOnFailure(_editor.GetDocData(1, ref iid, out var pointer));
            try { _lines = (IVsTextLines)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }

            _left = _registry.Adapters.GetDocumentBuffer((IVsTextBuffer)_lines);
            if (_left is null) { throw new InvalidOperationException("the baseline has no editor buffer"); }

            // The mode before the baseline, as Copilot's does: assigning the baseline is what draws, and it draws in the mode set.
            _model.Viewer.Options.SetOptionValue(ViewModeOption, mode);
            _window.Baseline = (IVsTextBuffer)_lines;

            _differences = _window.DifferenceBuffer;
            _differences.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            View.Closed += OnViewClosed;
            _entered = true;
            EnteredUtc = DateTime.UtcNow;
            _log.Debug(() => $"[changes] {Path} is shown in place ({mode})");

            _adornments = HunkAdornments.TryCreate(this, _window);
            OnDifferencesChanged();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentController.Enter", ex);
            Exit();
            return false;
        }
    }

    /// <summary>Put the window back as it was and let go of everything the review held. Safe to call twice. A window that is
    /// closing is not touched: there is nothing to put back.</summary>
    public void Exit(bool viewClosed = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Exited) { return; }
        Exited = true;

        try { if (_differences is not null) { _differences.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged; } }
        catch (Exception ex) { _log.Debug(() => $"[changes] could not stop listening to the difference of {Path}: {ex.Message}"); }
        View.Closed -= OnViewClosed;

        try { _adornments?.Dispose(); }
        catch (Exception ex) { _log.LogException("BaselineDocumentController.Exit(adornments)", ex); }
        _adornments = null;

        if (_entered && !viewClosed)
        {
            try { if (_window.Baseline is not null) { _window.Baseline = null; } }
            catch (Exception ex) { _log.Warn($"[changes] could not take the difference off {Path}: {ex.Message}"); }
        }

        ReleaseCom(ref _lines);
        ReleaseCom(ref _editor);
        _left = null;
        if (_scratch is not null)
        {
            try { File.Delete(_scratch); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug(() => $"[changes] could not delete {_scratch}: {ex.Message}");
            }
        }
        _registry.Detached(this, viewClosed);
    }

    /// <summary>Bring the window to the front, on the first hunk (as soon as there is one: the difference of a window that has
    /// only just been switched on is not computed yet).</summary>
    public void Reveal()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _frame.Show(); }
        catch (Exception ex) { _log.Debug(() => $"[changes] could not show the window of {Path}: {ex.Message}"); }
        if (Difference?.LineDifferences.Differences.Count > 0) { GoToHunk(ShownView(), 0); }
        else { _goToFirst = true; }
    }

    // The view that is on screen: the one inline view, or the document's own on the right. Not the document's view in the inline
    // form, which is there and not shown.
    private IWpfTextView ShownView()
    {
        var viewer = _model.Viewer;
        if (viewer is IDifferenceViewer2 two)
        {
            if (two.InlineViewExists && viewer.InlineView is not null) { return viewer.InlineView; }
            if (two.RightViewExists && viewer.RightView is not null) { return viewer.RightView; }
        }
        return View;
    }

    /// <summary>Move the caret of <paramref name="view"/> to the first hunk that starts after it, wrapping round.</summary>
    public void GoToNextHunk(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GoToHunk(view, null);
    }

    private void GoToHunk(IWpfTextView view, int? index)
    {
        var shown = FocusedView(view);
        if (!HunkLocator.TryGetModel(shown, out var model)) { return; }
        var difference = HunkLocator.Current(model);
        var hunks = HunkLocator.All(difference);
        if (hunks.Count == 0) { return; }

        var anchors = hunks.Select(h => HunkLocator.AnchorIn(shown, model, difference, h)?.Position ?? -1).ToList();
        var target = index ?? HunkNavigation.NextAfter(anchors, shown.Caret.Position.BufferPosition.Position);
        if (target < 0 || target >= anchors.Count || anchors[target] < 0) { return; }

        var point = new SnapshotPoint(shown.TextSnapshot, anchors[target]);
        shown.Caret.MoveTo(point);
        shown.Caret.EnsureVisible();
    }

    // The view the person is in, when there is one of the review's: in a side-by-side window the caret that matters is the
    // one on the side they are working in, and a second view can be in the way.
    private IWpfTextView FocusedView(IWpfTextView view)
    {
        if (view is not null && Matches(view) && HunkLocator.TryGetModel(view, out _)) { return view; }
        return View;
    }

    /// <summary>Keep one hunk: the document's text of it becomes the baseline's, so the hunk is no longer a difference.
    /// Nothing is written to the document. False when nothing was done.</summary>
    public async System.Threading.Tasks.Task<bool> KeepHunkAsync(Hunk hunk, IWpfTextView from = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited || IsBusy) { return false; }
        if (IsWholeFile)
        {
            await KeepAllAsync();
            return true;
        }

        try
        {
            if (!TryTranslate(hunk, out var left, out var right)) { return false; }
            if (!Replace(_left, left.Span, right.GetText())) { return false; }
            MarkBusy();
            _navigateIn = from;
            SaveScratch();

            // What the row keeps is now the baseline as the editor has it, in the encoding it was in.
            IsReplacingBaseline = true;
            try { await Owner.ReplaceBaselineAsync(Path, BaselineText.Encode(_left.CurrentSnapshot.GetText(), _baseline.Mark)); }
            finally { IsReplacingBaseline = false; }
            _goToNext = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentController.KeepHunkAsync", ex);
            return false;
        }
    }

    /// <summary>Undo one hunk: the baseline's text of it is written into the document, which is then saved the way the person's
    /// own save would, format on save included. False when nothing was done.</summary>
    public async System.Threading.Tasks.Task<bool> UndoHunkAsync(Hunk hunk, IWpfTextView from = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited || IsBusy) { return false; }
        if (IsWholeFile) { return await UndoAllAsync(); }

        try
        {
            if (!TryTranslate(hunk, out var left, out var right)) { return false; }
            var document = _document.TextBuffer;
            if (document.IsReadOnly(right.Span))
            {
                _log.Warn($"[changes] {Path} is read-only where this hunk is: it cannot be undone");
                return false;
            }
            if (!Replace(document, right.Span, left.GetText())) { return false; }
            MarkBusy();
            _navigateIn = from;
            _goToNext = true;

            await IdeContextService.Instance.SaveDocumentAsync(Path);
            await Owner.RemeasureAsync(Path);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentController.UndoHunkAsync", ex);
            return false;
        }
    }

    /// <summary>Keep every hunk: the review ends and the row goes, as the list's Keep does.</summary>
    public async Task KeepAllAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited) { return; }
        Exit();
        await Owner.Actions.KeepAsync(Path);
    }

    /// <summary>Undo every hunk the way the list's Undo does the file, so a created file is deleted, and a file the list
    /// has not measured is not overwritten. Says why when it could not.</summary>
    public async System.Threading.Tasks.Task<bool> UndoAllAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited) { return false; }

        // The document becomes the baseline while this runs, which is no hunks left: that must not end the review as a Keep
        // before the Undo has said whether it worked.
        _undoingAll = true;
        ChangeFailure failure;
        try { failure = await Owner.Actions.UndoAsync(Path); }
        finally { _undoingAll = false; }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (failure is not null)
        {
            ShellHelpers.ShowMessage($"{System.IO.Path.GetFileName(Path)} was not put back.\n\n{failure.Reason}", "Undo", warning: true);
            return false;
        }
        Exit();
        return true;
    }

    private void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _busyUntilUtc = default;
        OnDifferencesChanged();
    }

    private void OnDifferencesChanged()
    {
        if (Exited) { return; }
        var count = Difference?.LineDifferences.Differences.Count ?? 0;
        if (count > 0)
        {
            _sawDifferences = true;
        }
        else if (_sawDifferences && !_undoingAll)
        {
            // The last hunk was kept or undone, or the file was put back by hand: nothing is left to review.
            _log.Debug(() => $"[changes] nothing is left to review in {Path}");
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await KeepAllAsync();
            }).FileAndForget("BaselineDocumentController.Finish");
            return;
        }

        if (count > 0 && (_goToNext || _goToFirst))
        {
            var first = _goToFirst;
            var from = _navigateIn ?? ShownView();
            _goToNext = false;
            _goToFirst = false;
            _navigateIn = null;
            GoToHunk(from, first ? 0 : null);
        }
        (_adornments as HunkAdornments)?.Refresh();
    }

    private void OnViewClosed(object sender, EventArgs e) => Exit(viewClosed: true);

    private void MarkBusy() => _busyUntilUtc = DateTime.UtcNow + PendingLimit;

    /// <summary>The hunk's two spans, moved to the buffers' current snapshots, which have moved on since the difference was
    /// computed. False, and said, when a span is not in the buffer it should be: an edit made from the wrong place would be one
    /// somewhere else in the file.</summary>
    private bool TryTranslate(Hunk hunk, out SnapshotSpan left, out SnapshotSpan right)
    {
        left = default;
        right = default;
        if (!ReferenceEquals(hunk.Right.Snapshot.TextBuffer, _document.TextBuffer) || !ReferenceEquals(hunk.Left.Snapshot.TextBuffer, _left))
        {
            _log.Warn($"[changes] the difference of {Path} is not between the document and its baseline buffer: the hunk was not touched");
            return false;
        }
        right = hunk.Right.TranslateTo(_document.TextBuffer.CurrentSnapshot, SpanTrackingMode.EdgeExclusive);
        left = hunk.Left.TranslateTo(_left.CurrentSnapshot, SpanTrackingMode.EdgeExclusive);
        return true;
    }

    private bool Replace(ITextBuffer buffer, Span span, string text)
    {
        using var edit = buffer.CreateEdit();
        if (!edit.Replace(span, text))
        {
            edit.Cancel();
            _log.Warn($"[changes] the editor refused the edit of {Path}: the text there is read-only");
            return false;
        }
        edit.Apply();
        return true;
    }

    /// <summary>Make the scratch copy clean again, so that releasing it never asks about saving.</summary>
    private void SaveScratch()
    {
        try
        {
            if (_lines is IVsPersistDocData data) { data.SaveDocData(VSSAVEFLAGS.VSSAVE_SilentSave, out _, out _); }
        }
        catch (Exception ex)
        {
            _log.Debug(() => $"[changes] could not save the scratch copy of the baseline of {Path}: {ex.Message}");
        }
    }

    private static void ReleaseCom<T>(ref T com) where T : class
    {
        var held = com;
        com = null;
        if (held is null) { return; }
        try { Marshal.ReleaseComObject(held); }
        catch (Exception) { /* an object that is not a COM wrapper, or already released */ }
    }
}

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
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>One file's review in its own editor window: the document's code window switched into a difference view
/// against the baseline of its row, until the file has nothing left to review or the window goes.
/// <para>The baseline is shown from a scratch copy that the editor opens (so it is coloured like the document), and
/// every hunk action is a plain edit of one of the two buffers: <b>Keep</b> writes the document's text of the hunk into
/// the baseline's buffer, so the hunk is no longer a difference; <b>Undo</b> writes the baseline's text of the hunk into
/// the document, and saves it. Keep also has to survive the window: the baseline the row keeps is rewritten (see
/// <see cref="SessionChanges.ReplaceBaselineAsync"/>) in the file's own encoding and line ending, so that Open, the counts
/// and a later Undo of the whole file agree with what the editor shows.</para>
/// <para>The editor does not compare the document with the baseline's buffer itself but with a read-only projection of it that it
/// makes (one tracking span over all of it): the spans a difference holds on the left are spans of that mirror, and an edit
/// has to be made in the buffer behind it.</para>
/// <para>An action is only made from a difference that describes the buffers as they are now: the spans a difference holds
/// mean something only for the snapshots it was computed from, and an edit made with stale ones lands somewhere else. So an
/// action asked for in the moment between an edit and the recomputation that follows it is refused, not corrected.</para>
/// <para>Everything here runs on the UI thread. <see cref="BaselineDocumentRegistry"/> decides which file gets one.</para></summary>
internal sealed class BaselineDocumentController
{
    private const string ViewModeOption = "Diff/View/ViewMode";

    // After a Keep or an Undo the caret goes to the next hunk once the difference has been worked out again: for this long, and no
    // longer, so that a difference that never gets another hunk does not move the caret at some later edit of the person's.
    private static readonly TimeSpan NavigationWindow = TimeSpan.FromSeconds(5);

    // The same for the jump to the first hunk that opening the window asks for; longer, because the first difference of a big
    // file takes a while to be worked out.
    private static readonly TimeSpan RevealWindow = TimeSpan.FromSeconds(15);

    private readonly BaselineDocumentRegistry _registry;
    private readonly OutputWindowLogger _log = OutputWindowLogger.Global;
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
    private DifferenceViewMode _mode;
    private bool _entered;
    private bool _sawDifferences;
    private bool _actionRunning;
    private int _measureQueued;
    private DateTime _goToNextUntil;
    private DateTime _goToFirstUntil;
    private IWpfTextView _navigateIn;

    public BaselineDocumentController(BaselineDocumentRegistry registry, SessionChanges owner, FileChange row, IVsWindowFrame frame,
                                      IWpfTextView view, ITextDocument document, IVsDifferenceCodeWindow2 window,
                                      IDifferenceTextViewModel model, ReviewBaseline baseline)
    {
        _registry = registry;
        Owner = owner;
        Path = row.Path;
        RowSerial = row.Serial;
        Frame = frame;
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

    /// <summary>The window frame the review is in.</summary>
    public IVsWindowFrame Frame { get; }

    /// <summary>The document's own view, the one that existed before the window switched.</summary>
    public IWpfTextView View { get; }

    public bool Exited { get; private set; }

    /// <summary>The baseline of the row is being rewritten by this review, so the row is not a new one.</summary>
    public bool IsReplacingBaseline { get; private set; }

    /// <summary>The viewer of the view the review began in, which the window's buttons are drawn for.</summary>
    public IWpfDifferenceViewer Viewer => _model.Viewer;

    /// <summary>A hunk action is under way.</summary>
    public bool IsBusy => _actionRunning;

    /// <summary>The file did not exist before: its one hunk is the whole file, so Keep and Undo of a hunk are those of the file
    /// (an Undo deletes it, as the list's does).</summary>
    public bool IsWholeFile => _baseline.IsEmpty;

    /// <summary>Whether <paramref name="view"/> is one of this review's: the window's views, in any pane of it.</summary>
    public bool Matches(IWpfTextView view)
    {
        if (ReferenceEquals(view, View)) { return true; }
        if (!HunkLocator.TryGetModel(view, out var model)) { return false; }
        return ReferenceEquals(model.Viewer, _model.Viewer)
               || (_differences is not null && ReferenceEquals(model.Viewer?.DifferenceBuffer, _differences));
    }

    /// <summary>The current difference, or null before the viewer has computed one.</summary>
    public ISnapshotDifference Difference => _differences?.CurrentSnapshotDifference;

    /// <summary>Whether the hunk describes the two buffers as they are now. One computed before the last edit of either does not,
    /// and no action is made from it.</summary>
    public bool IsCurrent(Hunk hunk)
        => !Exited && _left is not null && _differences is not null
           && ReferenceEquals(_differences.BaseLeftBuffer, _left)
           && ReferenceEquals(hunk.Right.Snapshot, _document.TextBuffer.CurrentSnapshot)
           && ReferenceEquals(hunk.Left.Snapshot, _differences.LeftBuffer.CurrentSnapshot);

    /// <summary>Switch the window into a difference view, in <paramref name="mode"/>. False, with the editor left as it was,
    /// when anything on the way does not work: the registry then declines the file.</summary>
    public bool Enter(DifferenceViewMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // The baseline in the document's line endings: the two sides then differ in lines and not in how they end, and a
            // hunk moved between them brings no foreign line ending along. (A file that mixes the two is not reviewed.)
            var text = BaselineText.WithLineEnding(_baseline.Text, BaselineText.DominantLineEnding(View.TextSnapshot.GetText()));

            _scratch = Owner.Baselines.TargetFor(Owner.SessionId, Path);
            // UTF-8 with its mark, whatever the file is: this copy is read back by the editor, and with the mark it cannot
            // take accented text for a code page. The bytes the list keeps are written separately, in the file's own encoding.
            File.WriteAllBytes(_scratch, BaselineText.Encode(text, ChangeEvaluator.Mark.Utf8));

            // Not cached: the editor would keep the document after it is let go, and a later review of the file would be shown
            // what it held then and not what was just written.
            var manager = (IVsInvisibleEditorManager)Package.GetGlobalService(typeof(SVsInvisibleEditorManager));
            ErrorHandler.ThrowOnFailure(manager.RegisterInvisibleEditor(_scratch, null, 0, null, out _editor));
            var iid = typeof(IVsTextLines).GUID;
            ErrorHandler.ThrowOnFailure(_editor.GetDocData(1, ref iid, out var pointer));
            try { _lines = (IVsTextLines)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }

            _left = _registry.Adapters.GetDocumentBuffer((IVsTextBuffer)_lines);
            if (_left is null) { throw new InvalidOperationException("the baseline has no editor buffer"); }

            // The mode before the baseline, as Copilot's does: assigning the baseline is what draws, and it draws in the mode set.
            _mode = mode;
            _model.Viewer.Options.SetOptionValue(ViewModeOption, mode);
            _window.Baseline = (IVsTextBuffer)_lines;

            // From here on the window may hold the baseline: whatever fails next, Exit has to take it off again.
            _entered = true;
            // A baseline the editor cannot use is taken for none, without a word.
            if (_window.Baseline is null) { throw new InvalidOperationException("the window did not take the baseline"); }
            _differences = _window.DifferenceBuffer
                           ?? throw new InvalidOperationException("the window has no difference buffer once the baseline is set");
            if (!ReferenceEquals(_differences.BaseLeftBuffer, _left) || !ReferenceEquals(_differences.RightBuffer, _document.TextBuffer))
            {
                throw new InvalidOperationException("the difference is not between this document and its baseline");
            }

            // Lines as they are. By default the editor ignores white space at the ends of a line, and a line break is white space
            // to it: a final newline would make no difference, and neither would an edit of white space, which the file does
            // have, so the review would show a file with nothing to act on, and a hunk could end in the middle of a line.
            _differences.Options.SetOptionValue(DifferenceBufferOptions.IgnoreWhiteSpaceBehaviorId, IgnoreWhiteSpaceBehavior.None);

            _differences.SnapshotDifferenceChanged += OnSnapshotDifferenceChanged;
            ((IDifferenceBuffer2)_differences).BaseLeftBufferChanged += OnBaselineChanged;
            View.Closed += OnViewClosed;
            _document.FileActionOccurred += OnFileAction;
            _log.Info($"[changes] {Path} is shown in place ({mode})");

            _adornments = HunkAdornments.TryCreate(this);
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

    /// <summary>Show the review in another form (inline, side by side), without ending it.</summary>
    public void ApplyMode(DifferenceViewMode mode)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Exited || _mode == mode) { return; }
        try
        {
            _model.Viewer.Options.SetOptionValue(ViewModeOption, mode);
            _mode = mode;
        }
        catch (Exception ex)
        {
            _log.Warn($"[changes] could not switch {Path} to {mode}: {ex.Message}");
        }
    }

    /// <summary>Put the window back as it was and let go of everything the review held. Safe to call twice. A window that is
    /// closing may refuse to be touched, which is then not worth a line in the log; one that stays open and is left with a
    /// baseline nobody owns would refuse every later review of the file, so it is always tried.</summary>
    public void Exit(bool viewClosed = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Exited) { return; }
        Exited = true;

        try
        {
            try
            {
                if (_differences is not null)
                {
                    _differences.SnapshotDifferenceChanged -= OnSnapshotDifferenceChanged;
                    ((IDifferenceBuffer2)_differences).BaseLeftBufferChanged -= OnBaselineChanged;
                }
            }
            catch (Exception ex) { _log.Debug(() => $"[changes] could not stop listening to the difference of {Path}: {ex.Message}"); }
            View.Closed -= OnViewClosed;
            _document.FileActionOccurred -= OnFileAction;

            try { _adornments?.Dispose(); }
            catch (Exception ex) { _log.LogException("BaselineDocumentController.Exit(adornments)", ex); }
            _adornments = null;

            if (_entered)
            {
                // The option was this review's, and the buffer it is on outlives the review.
                try { _differences?.Options.ClearOptionValue(DifferenceBufferOptions.IgnoreWhiteSpaceBehaviorId); }
                catch (Exception ex) { _log.Debug(() => $"[changes] could not give back the white space option of {Path}: {ex.Message}"); }

                // Only a baseline that is this review's: another extension's (Copilot, a Git diff) may have replaced it, and taking
                // that off would take its difference away.
                try { if (HoldsOurBaseline()) { _window.Baseline = null; } }
                catch (Exception ex)
                {
                    if (viewClosed) { _log.Debug(() => $"[changes] the closing window of {Path} kept its baseline: {ex.Message}"); }
                    else { _log.Warn($"[changes] could not take the difference off {Path}: {ex.Message}"); }
                }
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
        }
        finally
        {
            // Whatever above failed, the registry must stop counting on a review that is over.
            _registry.Detached(this, viewClosed);
        }
    }

    /// <summary>Bring the window to the front, on the first hunk (as soon as there is one: the difference of a window that has
    /// only just been switched on is not computed yet).</summary>
    public void Reveal()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { Frame.Show(); }
        catch (Exception ex) { _log.Debug(() => $"[changes] could not show the window of {Path}: {ex.Message}"); }
        if (Difference?.LineDifferences.Differences.Count > 0) { GoToHunk(ShownView(), 0); }
        else { _goToFirstUntil = DateTime.UtcNow + RevealWindow; }
    }

    /// <summary>Move the caret of <paramref name="view"/> to the next hunk, wrapping round.</summary>
    public void GoToNextHunk(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GoToHunk(view, null, 1);
    }

    /// <summary>Move the caret of <paramref name="view"/> to the previous hunk, wrapping round.</summary>
    public void GoToPreviousHunk(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GoToHunk(view, null, -1);
    }

    private void GoToHunk(IWpfTextView view, int? index, int direction = 1)
    {
        var shown = FocusedView(view);
        if (!HunkLocator.TryGetModel(shown, out var model)) { return; }
        var difference = HunkLocator.Current(model);

        // Where each hunk starts in this view; a hunk with no place in it is not a place to go to.
        var anchors = new List<int>();
        var indexes = new List<int>();
        foreach (var hunk in HunkLocator.All(difference))
        {
            var anchor = HunkLocator.AnchorIn(shown, model, difference, hunk);
            if (anchor is null) { continue; }
            anchors.Add(anchor.Value.Position);
            indexes.Add(hunk.Index);
        }
        if (anchors.Count == 0) { return; }

        var target = index ?? Step(shown, anchors, indexes, direction);
        if (target < 0 || target >= anchors.Count) { return; }
        shown.Caret.MoveTo(new SnapshotPoint(shown.TextSnapshot, anchors[target]));
        shown.Caret.EnsureVisible();
    }

    // One hunk on from the one the caret is in; from a caret that is in none, by where it is.
    private static int Step(IWpfTextView view, IReadOnlyList<int> anchors, IReadOnlyList<int> indexes, int direction)
    {
        if (HunkLocator.TryGetHunkAtCaret(view, out var current))
        {
            for (var i = 0; i < indexes.Count; i++)
            {
                if (indexes[i] == current.Index) { return ((i + direction) % anchors.Count + anchors.Count) % anchors.Count; }
            }
        }
        var position = view.Caret.Position.BufferPosition.Position;
        return direction >= 0 ? HunkNavigation.NextAfter(anchors, position) : HunkNavigation.PreviousBefore(anchors, position);
    }

    /// <summary>How many hunks the difference has, and which of them the caret of <paramref name="view"/> is in (-1 when it is
    /// in none).</summary>
    public (int Index, int Count) ChangePosition(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var difference = Difference;
        if (difference is null) { return (-1, 0); }
        var hunks = HunkLocator.All(difference);
        var at = -1;
        if (view is not null && HunkLocator.TryGetHunkAtCaret(view, out var current))
        {
            for (var i = 0; i < hunks.Count; i++)
            {
                if (hunks[i].Index == current.Index) { at = i; break; }
            }
        }
        return (at, hunks.Count);
    }

    /// <summary>Which of the listed files this one is, and how many there are, in the order the list has them.</summary>
    public (int Index, int Count) FilePosition()
    {
        var rows = Owner.Set.Snapshot();
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Path, Path, StringComparison.OrdinalIgnoreCase)) { return (i, rows.Count); }
        }
        return (-1, rows.Count);
    }

    /// <summary>Show the file that is <paramref name="delta"/> places on in the list (wrapping round), the way Open does in the
    /// panel: in its own editor where that can be done.</summary>
    public async Task GoToFileAsync(int delta)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited) { return; }
        var rows = Owner.Set.Snapshot();
        var (at, count) = FilePosition();
        if (count < 2 || at < 0) { return; }
        await Owner.Actions.OpenAsync(rows[((at + delta) % count + count) % count].Path);
    }

    /// <summary>Bring the buttons and the panel up to date, for whoever knows that the window has been shown again.</summary>
    public void RefreshAdornments()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!Exited) { (_adornments as HunkAdornments)?.Refresh(); }
    }

    // The view the person is in, when there is one of the review's: in a side-by-side window the caret that matters is the
    // one on the side they are working in.
    private IWpfTextView FocusedView(IWpfTextView view)
    {
        if (view is not null && Matches(view) && HunkLocator.TryGetModel(view, out _)) { return view; }
        return ShownView();
    }

    // The view that is on screen: the one inline view in the inline form, the document's own on the right in the others. Not the
    // document's view in the inline form, which is there and not shown, and not the inline view in the others, which may still
    // exist after the form was changed.
    public IWpfTextView ShownView()
    {
        var viewer = _model.Viewer;
        if (viewer is IDifferenceViewer2 two)
        {
            var inline = ((IDifferenceViewer)viewer).ViewMode == DifferenceViewMode.Inline;
            if (inline && two.InlineViewExists && viewer.InlineView is not null) { return viewer.InlineView; }
            if (two.RightViewExists && viewer.RightView is not null) { return viewer.RightView; }
            if (two.InlineViewExists && viewer.InlineView is not null) { return viewer.InlineView; }
        }
        return View;
    }

    /// <summary>Keep one hunk: the document's text of it becomes the baseline's, so the hunk is no longer a difference.
    /// Nothing is written to the document. False when nothing was done.</summary>
    public async System.Threading.Tasks.Task<bool> KeepHunkAsync(Hunk hunk, IWpfTextView from = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited || _actionRunning) { return false; }
        if (IsWholeFile)
        {
            await KeepAllAsync();
            return true;
        }

        _actionRunning = true;
        try
        {
            if (!TryResolve(hunk, out var baseline, out var right)) { return false; }

            // Asked for before the edit, not after the awaits that follow it: the difference is recomputed meanwhile.
            ArmNavigation(from);
            if (!Replace(_left, baseline, right.GetText()))
            {
                DisarmNavigation();
                return false;
            }

            // The baseline as this hunk left it; and clean, so that letting go of it never asks about saving.
            var text = _left.CurrentSnapshot.GetText();
            MarkScratchClean();

            // What the row keeps is now the baseline as the editor has it, in the encoding and the line ending it came in.
            var bytes = BaselineText.Encode(BaselineText.WithLineEnding(text, _baseline.LineEnding), _baseline.Mark);
            IsReplacingBaseline = true;
            string stored;
            try { stored = await Owner.ReplaceBaselineAsync(Path, RowSerial, bytes); }
            finally { IsReplacingBaseline = false; }
            if (stored is null)
            {
                _log.Warn($"[changes] the hunk was kept in the editor, but the list could not take it into its baseline of {Path}");
                Say("The change was kept in the editor, but the list could not remember it.");
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentController.KeepHunkAsync", ex);
            return false;
        }
        finally
        {
            _actionRunning = false;
            Settled();
        }
    }

    /// <summary>Undo one hunk: the baseline's text of it is written into the document, which is then saved the way the person's
    /// own save would, format on save included. False when nothing was done.</summary>
    public async System.Threading.Tasks.Task<bool> UndoHunkAsync(Hunk hunk, IWpfTextView from = null)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Exited || _actionRunning) { return false; }
        if (IsWholeFile) { return await UndoAllAsync(); }

        _actionRunning = true;
        try
        {
            // A document with no unsaved edits is what is on disk. When it is not, the editor has not caught up with a write
            // (Claude's, a formatter's, git's), and saving would put its older text over the newer.
            if (!_document.IsDirty)
            {
                var matches = await BufferMatchesDiskAsync();
                if (matches is null)
                {
                    Say("The file could not be read just now: try again in a moment.");
                    return false;
                }
                if (matches == false)
                {
                    Say("The file changed on disk and the editor has not reloaded it yet: try again in a moment.");
                    return false;
                }
            }
            if (!TryResolve(hunk, out _, out var right)) { return false; }

            var document = _document.TextBuffer;
            if (document.IsReadOnly(right.Span))
            {
                _log.Warn($"[changes] {Path} is read-only where this hunk is: it cannot be undone");
                Say("This change cannot be undone here: the text is read-only.");
                return false;
            }

            ArmNavigation(from);
            if (!Replace(document, right.Span, hunk.Left.GetText()))
            {
                DisarmNavigation();
                return false;
            }

            await IdeContextService.Instance.SaveDocumentAsync(Path);
            if (_document.IsDirty)
            {
                _log.Warn($"[changes] {Path}: the hunk was undone in the editor, but saving the file failed");
                Say("The change was undone in the editor, but the file could not be saved.");
            }
            await Owner.RemeasureAsync(Path);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentController.UndoHunkAsync", ex);
            return false;
        }
        finally
        {
            _actionRunning = false;
            Settled();
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

        var failure = await Owner.Actions.UndoAsync(Path);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (failure is not null)
        {
            ShellHelpers.ShowMessage($"{System.IO.Path.GetFileName(Path)} was not put back.\n\n{failure.Reason}", "Undo", warning: true);
            return false;
        }
        Exit();
        return true;
    }

    // An action is over. The list may have changed while the flag was up, and a pass that saw it up left this review alone; and
    // the buttons, which stay hidden while an action runs, have no event of their own to come back on.
    private void Settled()
    {
        _registry.Poke();
        (_adornments as HunkAdornments)?.Refresh();
    }

    private void ArmNavigation(IWpfTextView from)
    {
        _navigateIn = from;
        _goToNextUntil = DateTime.UtcNow + NavigationWindow;
    }

    private void DisarmNavigation()
    {
        _navigateIn = null;
        _goToNextUntil = default;
        _goToFirstUntil = default;
    }

    private void OnSnapshotDifferenceChanged(object sender, SnapshotDifferenceChangeEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        OnDifferencesChanged();
    }

    private void OnDifferencesChanged()
    {
        if (Exited) { return; }
        var difference = Difference;
        if (difference is null) { return; }

        var count = difference.LineDifferences.Differences.Count;
        if (count > 0)
        {
            _sawDifferences = true;
        }
        else if (_sawDifferences)
        {
            // Nothing left to draw. Whether nothing is left to review is for the file to say: it is the file that was measured, not
            // the editor's picture of it. One that equals its baseline byte for byte leaves the list, and the registry then ends
            // the review; one that does not (an edit not saved yet, a different line ending) stays, with the review.
            RequestMeasure();
        }

        var now = DateTime.UtcNow;
        if (count > 0 && (now < _goToNextUntil || now < _goToFirstUntil))
        {
            var first = now < _goToFirstUntil;
            var from = _navigateIn ?? ShownView();
            DisarmNavigation();
            try { GoToHunk(from, first ? 0 : null); }
            catch (Exception ex) { _log.Debug(() => $"[changes] could not move to the next hunk of {Path}: {ex.Message}"); }
        }
        else if (count == 0)
        {
            // A difference without a hunk has nowhere to take the caret, and the edit of a later day is not the one that asked.
            DisarmNavigation();
        }
        (_adornments as HunkAdornments)?.Refresh();
    }

    private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
    {
        if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) { RequestMeasure(); }
    }

    // Measured a moment later, off the viewer's own dispatch: tearing the window down from inside its notification would leave
    // whatever else is listening running against a viewer that has just gone.
    private void RequestMeasure()
    {
        if (Interlocked.Exchange(ref _measureQueued, 1) == 1) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            Interlocked.Exchange(ref _measureQueued, 0);
            if (Exited) { return; }
            await Owner.RemeasureAsync(Path);
        }).FileAndForget("BaselineDocumentController.Measure");
    }

    private void OnViewClosed(object sender, EventArgs e) => Exit(viewClosed: true);

    // Another extension (Copilot, a Git diff) put its own baseline on the window while this review was on: the difference is
    // theirs now, and the buttons here would stand on their hunks. Ended a turn later, not inside the difference buffer's own
    // notification of the change.
    private void OnBaselineChanged(object sender, BaseLeftBufferChangedEventArgs e)
    {
        if (Exited || ReferenceEquals(e.NewBuffer, _left)) { return; }
        if (e.NewBuffer is null) { _log.Debug(() => $"[changes] the baseline of {Path} was taken off the window: the review ends"); }
        else { _log.Info($"[changes] the difference of {Path} was replaced by another extension's: the review ends"); }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            Exit();
        }).FileAndForget("BaselineDocumentController.BaselineReplaced");
    }

    // The window's baseline is the one this review gave it: the buffer behind it is the one that was opened for the review.
    private bool HoldsOurBaseline()
    {
        var current = _window.Baseline;
        return current is not null && _left is not null && ReferenceEquals(_registry.Adapters.GetDocumentBuffer(current), _left);
    }

    /// <summary>The hunk's two spans, when they can be acted on: both in the buffers they should be in, both from the buffers as
    /// they are now, and both made of whole lines. <paramref name="baseline"/> is where the hunk is in the baseline's own buffer,
    /// which is the one an edit goes to. False, and said, when not.</summary>
    private bool TryResolve(Hunk hunk, out Span baseline, out SnapshotSpan right)
    {
        baseline = default;
        var left = hunk.Left;
        right = hunk.Right;
        if (_differences is null
            || !ReferenceEquals(right.Snapshot.TextBuffer, _document.TextBuffer)
            || !ReferenceEquals(left.Snapshot.TextBuffer, _differences.LeftBuffer))
        {
            _log.Warn($"[changes] the difference of {Path} is not between the document and its baseline: the hunk was not touched");
            Say("This change cannot be handled here: the difference is not the one this review made.");
            return false;
        }
        if (!IsCurrent(hunk))
        {
            Say("The difference is being worked out again: try once more in a moment.");
            return false;
        }
        if (!HunkLocator.IsWholeLines(left) || !HunkLocator.IsWholeLines(right))
        {
            _log.Warn($"[changes] a hunk of {Path} does not start and end at line boundaries, so it was not touched: an edit from it would join lines");
            Say("This change cannot be handled here: it does not start and end at line boundaries.");
            return false;
        }
        if (!TryMapToBaseline(left, out baseline))
        {
            _log.Warn($"[changes] the baseline's mirror in the difference of {Path} is not the whole of its buffer as it is now: the hunk was not touched");
            Say("This change cannot be handled here: the difference is not the one this review made.");
            return false;
        }
        return true;
    }

    // The mirror is one tracking span over the whole baseline, so a span of it is the same offsets in the buffer, as long as the
    // buffer is the version the mirror shows. Anything else is a shape of the editor this was not written for, and no edit is
    // made from it.
    private bool TryMapToBaseline(SnapshotSpan mirrored, out Span baseline)
    {
        baseline = default;
        if (_left is null || mirrored.Snapshot is not IProjectionSnapshot projection) { return false; }
        var sources = projection.GetSourceSpans();
        var current = _left.CurrentSnapshot;
        if (sources.Count != 1
            || !ReferenceEquals(sources[0].Snapshot, current)
            || sources[0].Start.Position != 0
            || sources[0].Length != current.Length
            || projection.Length != current.Length)
        {
            return false;
        }
        baseline = mirrored.Span;
        return true;
    }

    /// <summary>Whether the editor holds the file as it is on disk: null when the disk could not be read.</summary>
    private async System.Threading.Tasks.Task<bool?> BufferMatchesDiskAsync()
    {
        var bytes = await System.Threading.Tasks.Task.Run(() =>
        {
            try { return ChangeEvaluator.ReadShared(Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Debug(() => $"[changes] could not read {Path} to compare it with the editor: {ex.Message}");
                return null;
            }
        });
        if (bytes is null) { return null; }
        var disk = ChangeEvaluator.DecodeStrict(bytes);
        return disk is not null && string.Equals(disk, _document.TextBuffer.CurrentSnapshot.GetText(), StringComparison.Ordinal);
    }

    private bool Replace(ITextBuffer buffer, Span span, string text)
    {
        using var edit = buffer.CreateEdit();
        if (!edit.Replace(span, text))
        {
            edit.Cancel();
            _log.Warn($"[changes] the editor refused the edit of {Path}: the text there is read-only");
            Say("This change cannot be handled here: the text is read-only.");
            return false;
        }
        edit.Apply();
        return true;
    }

    /// <summary>Mark the scratch copy clean again, so that releasing it never asks about saving. Not saved: a save runs the
    /// editor's own clean-up (a final newline, trimmed white space) over the baseline, and the baseline is not the person's text.</summary>
    private void MarkScratchClean()
    {
        try
        {
            if (_lines is not null && _lines.GetStateFlags(out var flags) == VSConstants.S_OK)
            {
                _lines.SetStateFlags(flags & ~(uint)BUFFERSTATEFLAGS.BSF_MODIFIED);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(() => $"[changes] could not mark the scratch copy of the baseline of {Path} clean: {ex.Message}");
        }
    }

    // A word in the status bar: a refusal the person has to be told of, not worth a dialog.
    private static void Say(string text)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar) { bar.SetText(text); }
        }
        catch (Exception)
        {
            // The status bar is a courtesy.
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

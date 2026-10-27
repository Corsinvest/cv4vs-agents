/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// IVsDifferenceCodeWindow2 is provisional ([Obsolete]) in the SDK. It is only read here (is the window ours to put a difference
// in, where is the first change), and the review is declined where it does not answer.
#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>How the start of a review ended.</summary>
internal enum ReviewStart
{
    /// <summary>The review is on.</summary>
    Started,

    /// <summary>Not now: the editor and the disk disagree, or the document has changes that are not saved. Another try may do.</summary>
    NotYet,

    /// <summary>Not this file, for the reason given.</summary>
    Declined,
}

/// <summary>One file's review in its own editor: GitHub Copilot's review UI, drawn by Copilot, for the baseline of its row.
/// <para>Copilot copies the open document's text as the baseline when it makes the state of a file, and takes no other text. So
/// the state is made first, then the two copies it keeps of the baseline are rewritten with the row's: the buffer the hunk buttons
/// edit, and the temp file the code window shows on the left. The document is not touched, which is why nothing is added to its
/// undo history. Copilot's own order matters (see <see cref="StartAsync"/>).</para>
/// <para>What the person does with Copilot's buttons is seen through the state's events. A hunk <b>kept</b> rewrites the baseline
/// Copilot holds, and the row's baseline follows it (<see cref="SessionChanges.ReplaceBaselineAsync"/>), so that the counts, Open and
/// a later Undo of the whole file agree with what the editor shows. A hunk <b>undone</b> edits the document and saves it; the save
/// is what the row is measured again on. <b>Keep all</b> and <b>Undo all</b> end the state, and the document tells which of them it
/// was.</para>
/// <para>Everything here runs on the UI thread. <see cref="ReviewRegistry"/> decides which file gets one.</para></summary>
internal sealed class CopilotReview
{
    // The client Copilot records for a review that is ours, in its telemetry.
    private const string ClientName = "cv4vs.claude";

    // How long the first difference is waited for, after Copilot has been asked to draw. It is worked out a moment after the
    // view is wired, and the review is not declared on before the editor shows it.
    private static readonly TimeSpan DifferenceWait = TimeSpan.FromSeconds(2);

    // Opening the window jumps to the first change as soon as there is one, for this long; longer than the wait above because the
    // first difference of a big file takes a while to be worked out.
    private static readonly TimeSpan RevealWindow = TimeSpan.FromSeconds(15);

    // What Copilot's Undo all does to the document and the disk takes a moment after it says it is ending the review.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(300);

    private enum Phase
    {
        Starting,
        Active,
        Ended,
    }

    private readonly ReviewRegistry _registry;
    private readonly CopilotReviewShape _shape;
    private readonly OutputWindowLogger _log = OutputWindowLogger.Global;
    private readonly IVsDifferenceCodeWindow2 _window;
    private readonly ITextDocument _document;
    private readonly ReviewBaseline _baseline;
    private readonly List<IDisposable> _subscriptions = new();

    // Completed when Copilot has let go of the file: what a caller that asked for the end waits for.
    private readonly System.Threading.Tasks.TaskCompletionSource<bool> _over
        = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    private Phase _phase = Phase.Starting;
    private object _state;
    private string _lastBaselineText;
    private bool _endWhenStarted;
    private bool _replacePending;
    private bool _replaceRunning;
    private int _measureQueued;
    private DateTime _revealUntil;
    private bool _revealRunning;

    public CopilotReview(ReviewRegistry registry, SessionChanges owner, FileChange row, IVsWindowFrame frame, IWpfTextView view,
                         ITextDocument document, IVsDifferenceCodeWindow2 window, ReviewBaseline baseline, CopilotReviewShape shape)
    {
        _registry = registry;
        Owner = owner;
        Path = row.Path;
        RowSerial = row.Serial;
        Frame = frame;
        View = view;
        _document = document;
        _window = window;
        _baseline = baseline;
        _shape = shape;
    }

    public SessionChanges Owner { get; }
    public string Path { get; }

    /// <summary>Which row this review began with: when the row is replaced (a Keep, then another edit) the review is too.</summary>
    public long RowSerial { get; }

    /// <summary>The window frame the review is in.</summary>
    public IVsWindowFrame Frame { get; }

    /// <summary>The document's own view, the one the review was started on.</summary>
    public IWpfTextView View { get; }

    /// <summary>The review is on: started, and not ended.</summary>
    public bool IsActive => _phase == Phase.Active;

    /// <summary>The review is over, or is being taken down.</summary>
    public bool IsEnded => _phase == Phase.Ended;

    /// <summary>The baseline of the row is being rewritten by this review (a hunk was kept), so the row is not a new one.</summary>
    public bool IsReplacingBaseline { get; private set; }

    // --- start -------------------------------------------------------------------------------------------

    /// <summary>Put Copilot's review on the document, against the baseline of the row. Not declared on until Copilot has drawn.
    /// <para>The order is Copilot's own, and each step is there for a reason found in its code: the state first (it copies the
    /// document as the baseline); then its two copies of the baseline are rewritten; then it is told there is a change, because
    /// its view model works out the first difference at attach time only when it has been told so; and only then is the view
    /// wired. Telling it afterwards would draw nothing until the document next changed.</para></summary>
    public async System.Threading.Tasks.Task<(ReviewStart Result, string Why)> StartAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        View.Closed += OnViewClosed;
        try
        {
            var notReady = await NotReadyAsync();
            if (notReady is not null) { return Finish(ReviewStart.NotYet, notReady); }

            // States are Copilot's, keyed by path, and shared with whoever asks: one that is there is somebody else's.
            if (_shape.TryGetState(Path) is not null) { return Finish(ReviewStart.Declined, "GitHub Copilot is already reviewing it"); }

            // In the document's line ending, so that the two sides differ in lines and not in how they end, and a hunk moved
            // between them brings no foreign line ending along.
            _lastBaselineText = BaselineText.WithLineEnding(_baseline.Text, BaselineText.DominantLineEnding(View.TextSnapshot.GetText()));
            if (string.IsNullOrEmpty(_lastBaselineText)) { return Finish(ReviewStart.Declined, "there is no earlier text to show a difference against"); }

            var id = _shape.NewInteractionId(ClientName);
            _state = await _shape.GetOrCreateAsync(Path, id);
            if (_state is null) { return Finish(ReviewStart.Declined, "GitHub Copilot made no review state for it"); }
            Subscribe();

            var inert = ((ITextSnapshot)_shape.BaselineSnapshot(_state)).TextBuffer;
            Replace(inert, _lastBaselineText);
            // With a byte-order mark, as Copilot writes it itself: the code window reads this file back as UTF-8 whatever it holds.
            File.WriteAllText(_shape.BaselineFilePath(_state), _lastBaselineText, new UTF8Encoding(true));

            _shape.UpdateDiffState(_state, _shape.NewDiffState(0, 0, 1));
            await _shape.MarkAsync(View, id);
            await ObserveAsync();
            _shape.MarkCompleted(View);

            // Gone already: Copilot found nothing to draw and cleaned up after itself.
            if (!ReferenceEquals(_shape.TryGetState(Path), _state))
            {
                await AbandonAsync();
                return Finish(ReviewStart.Declined, "GitHub Copilot found no difference to show");
            }

            _document.FileActionOccurred += OnFileAction;
            _phase = Phase.Active;
            _log.Info($"[changes] {Path} is reviewed in the editor, with Copilot's own Keep and Undo");
            if (_endWhenStarted)
            {
                await EndCoreAsync(viewClosed: false);
                return (ReviewStart.Declined, null);
            }
            return (ReviewStart.Started, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A view that was closed under the start is not a failure the person has to be told of.
            var closed = _endWhenStarted || View.IsClosed;
            if (closed) { _log.Debug(() => $"[changes] the review of {Path} was cut short by its view closing: {ex.Message}"); }
            else { _log.LogException("CopilotReview.StartAsync", ex); }
            await AbandonAsync();
            return Finish(ReviewStart.Declined, closed ? null : $"GitHub Copilot's review failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private (ReviewStart Result, string Why) Finish(ReviewStart result, string why)
    {
        // Not started: nothing is held, and the registry forgets this review. What it was told to end meanwhile is moot.
        View.Closed -= OnViewClosed;
        UnhookCopilot();
        _phase = Phase.Ended;
        _over.TrySetResult(true);
        return (result, why);
    }

    // Not now: a document with unsaved changes shows more than the disk, and one the editor has not reloaded since Claude wrote
    // it shows less. Either way the difference would not be the one the row measured, and a hunk undone in it would save over a
    // file that is newer.
    private async System.Threading.Tasks.Task<string> NotReadyAsync()
    {
        if (_document.IsDirty) { return "it has changes that are not saved"; }
        var matches = await BufferMatchesDiskAsync();
        if (matches is null) { return "the file could not be read just now"; }
        return matches == false ? "the editor shows an older file than the disk" : null;
    }

    // Looks at the window, not at events: Copilot's own come on whichever thread worked the difference out.
    private async Task ObserveAsync()
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < DifferenceWait)
        {
            await Task.Delay(50);
            if (_window.Baseline is not null && DifferenceCount() > 0) { break; }
        }
        _log.Debug(() => $"[changes] Copilot drew the difference of {Path} after {clock.ElapsedMilliseconds} ms");
    }

    private int DifferenceCount()
    {
        try { return _window.DifferenceBuffer?.CurrentSnapshotDifference?.LineDifferences?.Differences.Count ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // A window that is being made or taken down answers with a failure: that is "not yet".
            _log.Debug(() => $"[changes] the difference of {Path} cannot be read yet: {ex.Message}");
            return 0;
        }
    }

    // --- end ---------------------------------------------------------------------------------------------

    /// <summary>End the review: the diff goes and Copilot lets go of its copy of the baseline. The document is not touched. A
    /// review that is still starting is ended as soon as it has started, and this returns when it is over. Safe to call twice.</summary>
    public async Task EndAsync(bool viewClosed = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_phase == Phase.Active)
        {
            await EndCoreAsync(viewClosed);
            return;
        }
        // Starting: it is ended the moment it has started. Ended: it is being taken down by whoever began to. Either way this is
        // over when Copilot has let go, and not before: the caller is about to rewrite the file.
        if (_phase == Phase.Starting) { _endWhenStarted = true; }
        await Task.WhenAny(_over.Task, Task.Delay(TimeSpan.FromSeconds(8)));
    }

    private async Task EndCoreAsync(bool viewClosed)
    {
        _phase = Phase.Ended;
        View.Closed -= OnViewClosed;
        _document.FileActionOccurred -= OnFileAction;
        // Ours first: the dismissal below is answered by an event that is not news to anybody here.
        UnhookCopilot();
        try { await DismissAsync(viewClosed); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"[changes] could not end the review of {Path} in Copilot: {ex.Message}");
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _registry.Detached(this, viewClosed);
            _over.TrySetResult(true);
        }
    }

    // The state is only ended when it is still ours: Copilot's own flow may have taken the file over meanwhile, and ending that
    // would take its difference away. A state no view model listens to does not end on a dismissal, and is released instead.
    private async Task DismissAsync(bool viewClosed)
    {
        if (!ReferenceEquals(_shape.TryGetState(Path), _state)) { return; }
        _shape.RequestDismissal(_state);
        await WaitForAsync(() => !ReferenceEquals(_shape.TryGetState(Path), _state), viewClosed ? 400 : 1500);
        if (ReferenceEquals(_shape.TryGetState(Path), _state)) { await _shape.ReleaseAsync(Path); }
    }

    // A start that failed: what Copilot holds for it goes, and the document, which was never touched, stays as it was.
    private async Task AbandonAsync()
    {
        try
        {
            UnhookCopilot();
            if (_state is not null) { await DismissAsync(viewClosed: false); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"[changes] could not take Copilot's review of {Path} down: {ex.Message}");
        }
    }

    // --- what Copilot tells -----------------------------------------------------------------------------

    private void Subscribe()
    {
        _subscriptions.Add(_shape.OnDiffChanged(_state, changes => Post(() => OnDifferenceChanged(changes))));
        _subscriptions.Add(_shape.OnBaselineChanged(_state, () => Post(() =>
        {
            OnBaselineChanged();
            return Task.CompletedTask;
        })));
        _subscriptions.Add(_shape.OnDismissRequested(_state, () => Post(OnDismissedAsync)));
    }

    private void UnhookCopilot()
    {
        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); }
            catch (Exception ex) { _log.Debug(() => $"[changes] could not stop listening to Copilot's review of {Path}: {ex.Message}"); }
        }
        _subscriptions.Clear();
    }

    // Copilot raises them on whichever thread it works on, and before whatever it does next: handled a turn later, on the UI
    // thread, not inside its own notification.
    private void Post(Func<Task> work)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            await work();
        }).FileAndForget("CopilotReview.Event");
    }

    private Task OnDifferenceChanged(int changes)
    {
        // No hunk left: whether nothing is left to review is for the file to say, not for the editor's picture of it. One that
        // equals its baseline byte for byte leaves the list, and the registry then ends the review.
        if (_phase == Phase.Active && changes == 0) { RequestMeasure(); }
        return Task.CompletedTask;
    }

    // A hunk was kept: Copilot wrote the document's text of it into its baseline. The baseline the row keeps follows, in the
    // encoding and the line ending it came in, so that the counts, Open and an Undo of the whole file agree with the editor.
    // Several in a row are one pass that reads the latest.
    private void OnBaselineChanged()
    {
        if (_phase != Phase.Active) { return; }
        _replacePending = true;
        if (_replaceRunning) { return; }
        _replaceRunning = true;
        ThreadHelper.JoinableTaskFactory.RunAsync(ReplaceBaselineLoopAsync).FileAndForget("CopilotReview.Baseline");
    }

    private async Task ReplaceBaselineLoopAsync()
    {
        try
        {
            while (_replacePending && _phase == Phase.Active)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _replacePending = false;
                var text = ((ITextSnapshot)_shape.BaselineSnapshot(_state)).GetText();
                // A hunk undone raises it too, and the baseline is what it was.
                if (string.Equals(text, _lastBaselineText, StringComparison.Ordinal)) { continue; }
                _lastBaselineText = text;

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
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("CopilotReview.ReplaceBaselineLoopAsync", ex);
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _replaceRunning = false;
            // The list may have changed while the flag was up, and a pass that saw it up left this review alone.
            _registry.Poke();
        }
    }

    // Copilot's Keep all and Undo all end the state, and so does anything else that asks it to. The document says which it was:
    // Undo all puts the baseline into it, Keep all leaves it as it is. Looked at a moment later, once the edit and the save that
    // Undo all makes are done.
    private async Task OnDismissedAsync()
    {
        if (_phase != Phase.Active) { return; }
        _phase = Phase.Ended;
        View.Closed -= OnViewClosed;
        _document.FileActionOccurred -= OnFileAction;
        UnhookCopilot();
        // Copilot has ended it itself: nobody has to wait for this review any more, and Keep below asks for its end.
        _over.TrySetResult(true);
        try
        {
            await Task.Delay(SettleTime);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var undone = string.Equals(_document.TextBuffer.CurrentSnapshot.GetText(), _lastBaselineText, StringComparison.Ordinal);
            _log.Info($"[changes] Copilot ended the review of {Path}: {(undone ? "undone, the document is the baseline" : "kept as it is")}");
            if (undone) { await Owner.RemeasureAsync(Path); }
            else { await Owner.Actions.KeepAsync(Path); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("CopilotReview.OnDismissedAsync", ex);
        }
        finally
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _registry.Detached(this, viewClosed: false);
        }
    }

    // A hunk undone is saved by Copilot: the row is measured on the file as it is, and a file that is back to its baseline leaves
    // the list.
    private void OnFileAction(object sender, TextDocumentFileActionEventArgs e)
    {
        if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0) { RequestMeasure(); }
    }

    // Measured a moment later, off Copilot's own dispatch: tearing the review down from inside its notification would leave
    // whatever else is listening running against a view that has just gone.
    private void RequestMeasure()
    {
        if (Interlocked.Exchange(ref _measureQueued, 1) == 1) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            Interlocked.Exchange(ref _measureQueued, 0);
            if (_phase != Phase.Active) { return; }
            await Owner.RemeasureAsync(Path);
        }).FileAndForget("CopilotReview.Measure");
    }

    private void OnViewClosed(object sender, EventArgs e)
        => Post(() => EndAsync(viewClosed: true));

    // --- moving about -----------------------------------------------------------------------------------

    /// <summary>Bring the window to the front, on the first change (as soon as there is one: the difference of a window that has
    /// only just been switched on is not worked out yet).</summary>
    public void Reveal()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { Frame.Show(); }
        catch (Exception ex) { _log.Debug(() => $"[changes] could not show the window of {Path}: {ex.Message}"); }
        _revealUntil = DateTime.UtcNow + RevealWindow;
        if (_revealRunning) { return; }
        _revealRunning = true;
        ThreadHelper.JoinableTaskFactory.RunAsync(RevealLoopAsync).FileAndForget("CopilotReview.Reveal");
    }

    private async Task RevealLoopAsync()
    {
        try
        {
            while (_phase != Phase.Ended && DateTime.UtcNow < _revealUntil)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (TryGoToFirstChange()) { return; }
                await Task.Delay(100);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug(() => $"[changes] could not move to the first change of {Path}: {ex.Message}");
        }
        finally
        {
            _revealRunning = false;
        }
    }

    // Where the first difference starts in the view that is on screen: the inline view draws the deleted lines among the
    // document's, so a change starts where its deleted lines do, or where its added ones do when it deleted none.
    private bool TryGoToFirstChange()
    {
        var model = View.TextViewModel as IDifferenceTextViewModel;
        var viewer = model?.Viewer;
        var difference = viewer?.DifferenceBuffer?.CurrentSnapshotDifference;
        var lines = difference?.LineDifferences;
        if (lines is null || lines.Differences.Count == 0) { return false; }

        var shown = ShownView(viewer);
        var first = lines.Differences[0];
        if (!TrySpan(difference.LeftBufferSnapshot, lines.LeftDecomposition, first.Left, out var left)
            || !TrySpan(difference.RightBufferSnapshot, lines.RightDecomposition, first.Right, out var right))
        {
            return false;
        }

        SnapshotPoint point;
        switch ((shown.TextViewModel as IDifferenceTextViewModel)?.ViewType)
        {
            case DifferenceViewType.LeftView:
                point = left.Start;
                break;
            case DifferenceViewType.RightView:
                point = right.Start;
                break;
            default:
                point = difference.MapToInlineSnapshot(left.Length != 0 ? left.Start : right.Start);
                break;
        }
        if (point.Snapshot is null) { return false; }

        shown.Caret.MoveTo(point.TranslateTo(shown.TextSnapshot, PointTrackingMode.Negative));
        shown.Caret.EnsureVisible();
        return true;
    }

    // A difference counts lines; this turns its range of lines into the span of text they are. An empty buffer has no text to
    // point into: its span is an empty one at the start. A range that is not inside what was tokenised has no span.
    private static bool TrySpan(ITextSnapshot snapshot, ITokenizedStringList decomposition, Span lines, out SnapshotSpan span)
    {
        span = default;
        if (lines.Start < 0 || lines.End > ((ICollection<string>)decomposition).Count) { return false; }
        if (snapshot.Length == 0)
        {
            span = new SnapshotSpan(snapshot, 0, 0);
            return true;
        }
        try
        {
            span = new SnapshotSpan(snapshot, decomposition.GetSpanInOriginal(lines));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    // The view that is on screen: the one inline view in the inline form, the document's own on the right in the others. Not the
    // document's view in the inline form, which is there and not shown, and not the inline view in the others, which may still
    // exist after the form was changed.
    private IWpfTextView ShownView(IWpfDifferenceViewer viewer)
    {
        if (viewer is IDifferenceViewer2 two)
        {
            var inline = ((IDifferenceViewer)viewer).ViewMode == DifferenceViewMode.Inline;
            if (inline && two.InlineViewExists && viewer.InlineView is not null) { return viewer.InlineView; }
            if (two.RightViewExists && viewer.RightView is not null) { return viewer.RightView; }
            if (two.InlineViewExists && viewer.InlineView is not null) { return viewer.InlineView; }
        }
        return View;
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
    /// panel: with Copilot's review where that can be done.</summary>
    public async Task GoToFileAsync(int delta)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_phase == Phase.Ended) { return; }
        var rows = Owner.Set.Snapshot();
        var (at, count) = FilePosition();
        if (count < 2 || at < 0) { return; }
        await Owner.Actions.OpenAsync(rows[((at + delta) % count + count) % count].Path);
    }

    // --- the document ------------------------------------------------------------------------------------

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

    private static void Replace(ITextBuffer buffer, string text)
    {
        using var edit = buffer.CreateEdit();
        if (!edit.Replace(0, buffer.CurrentSnapshot.Length, text))
        {
            edit.Cancel();
            throw new InvalidOperationException("the editor refused the edit of Copilot's copy of the baseline");
        }
        edit.Apply();
    }

    private static async Task WaitForAsync(Func<bool> condition, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < milliseconds) { await Task.Delay(50); }
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
}

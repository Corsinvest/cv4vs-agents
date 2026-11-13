/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// IVsDifferenceCodeWindow2 is provisional ([Obsolete]) in the SDK. It is only read here (is the window ours to put a difference
// in, where is the first change), and the review is declined where it does not answer.
#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly TaskCompletionSource<bool> _over
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Phase _phase = Phase.Starting;
    private IDifferenceBuffer _differences;
    private string _tempPath;
    private object _state;
    private string _lastBaselineText;
    private bool _endWhenStarted;
    private bool _replacePending;
    private bool _replaceRunning;
    private int _measureQueued;
    private DateTime _revealUntil;
    private bool _revealRunning;

    // The last edit of the document while the review is on, and the text an Undo of Copilot's put back with it, until the editor
    // has been made to show that text (see ShowPutBack).
    private TextContentChangedEventArgs _lastEdit;
    private ITrackingPoint _putBackStart;
    private ITrackingPoint _putBackEnd;
    private int _putBackVersion;

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
    public async Task<(ReviewStart Result, string Why)> StartAsync()
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
            _tempPath = _shape.BaselineFilePath(_state);
            // A copy of the baseline that an earlier review of this file left open would be shown in place of the file written below.
            CloseBaselineDocument();

            var inert = ((ITextSnapshot)_shape.BaselineSnapshot(_state)).TextBuffer;
            Replace(inert, _lastBaselineText);
            // With a byte-order mark, as Copilot writes it itself: the code window reads this file back as UTF-8 whatever it holds.
            await AsyncFile.WriteAllTextAsync(_tempPath, _lastBaselineText, new UTF8Encoding(true));

            // Before Copilot gives the window its baseline: the first difference it works out is then the exact one, and nothing is
            // redrawn after it (the inline view lost its deleted lines when the option came a moment after the first drawing).
            CompareLinesExactly();
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

            // What Copilot's buttons act on is the left side of the difference: its spans are applied to Copilot's own copy of the
            // baseline. A left side that is not that copy (a stale document) would have Undo write the wrong text, or none, over
            // the file, so a review that is not drawn against the baseline it made is not left on the screen.
            if (!await LeftSideIsOursAsync())
            {
                _log.Warn($"[changes] the left side of Copilot's difference of {Path} is not the baseline this review made ({LeftText()?.Length.ToString() ?? "none"} characters, not {_lastBaselineText.Length}): the review is not shown");
                await AbandonAsync();
                return Finish(ReviewStart.Declined, "the left side of the difference is not the baseline this review made");
            }

            _document.FileActionOccurred += OnFileAction;
            _document.TextBuffer.Changed += OnDocumentChanged;
            if (_differences is not null) { _differences.SnapshotDifferenceChanged += OnDifferenceWorkedOut; }
            DiscardUndoHistory("the review started");
            _phase = Phase.Active;
            _log.Info($"[changes] {Path} is reviewed in the editor, with Copilot's own Keep and Undo");
            _log.Debug(() => $"[changes] {Path}: the document has {_document.TextBuffer.CurrentSnapshot.Length} characters, the baseline "
                             + $"{_lastBaselineText.Length}, Copilot's copy {((ITextSnapshot)_shape.BaselineSnapshot(_state)).Length}, the left side {LeftText()?.Length.ToString() ?? "none"}; "
                             + $"line ending {(BaselineText.DominantLineEnding(_lastBaselineText) == BaselineText.Crlf ? "CRLF" : "LF or none")}");
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
        GiveBackDifferenceOptions();
        _phase = Phase.Ended;
        _over.TrySetResult(true);
        return (result, why);
    }

    // Not now: a document with unsaved changes shows more than the disk, and one the editor has not reloaded since Claude wrote
    // it shows less. Either way the difference would not be the one the row measured, and a hunk undone in it would save over a
    // file that is newer.
    private async Task<string> NotReadyAsync()
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
            CompareLinesExactly();
            if (_window.Baseline is not null && DifferenceCount() > 0) { break; }
        }
        _log.Debug(() => $"[changes] Copilot drew the difference of {Path} after {clock.ElapsedMilliseconds} ms");
    }

    private string LeftText()
    {
        try { return _window.DifferenceBuffer?.LeftBuffer?.CurrentSnapshot?.GetText(); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            _log.Debug(() => $"[changes] the left side of the difference of {Path} cannot be read yet: {ex.Message}");
            return null;
        }
    }

    // The editor loads the left side from the file a moment after it is given: waited for, not assumed.
    private async Task<bool> LeftSideIsOursAsync()
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (string.Equals(LeftText(), _lastBaselineText, StringComparison.Ordinal)) { return true; }
            if (clock.Elapsed > TimeSpan.FromMilliseconds(1500)) { return false; }
            await Task.Delay(50);
        }
    }

    // Copilot opens its temp copy of the baseline in the running document table with an edit lock, and when a review ends it forgets
    // that lock without letting go of it (its frame handler's ReleaseFrame drops only its own record of the cookie). The document
    // stays open for good with the text that review had, and the next review of a file whose temp copy is named the same is handed
    // that document and not the file: the left side of the difference is another review's baseline, and the buttons act on spans of
    // it. It is closed here, by letting go of the locks that are left, before a review starts and after it ends. Not while a window
    // shows it, nor while the difference still has it as its baseline.
    private void CloseBaselineDocument()
    {
        if (string.IsNullOrEmpty(_tempPath)) { return; }
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_window.Baseline is not null || IdeContextService.FindDocumentFrames(_tempPath).Count > 0) { return; }
            if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable rdt) { return; }
            for (var i = 0; i < 8; i++)
            {
                var found = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, _tempPath, out _, out _, out var data, out var cookie);
                if (data != IntPtr.Zero) { Marshal.Release(data); }
                if (ErrorHandler.Failed(found) || cookie == VSConstants.VSCOOKIE_NIL) { return; }

                rdt.GetDocumentInfo(cookie, out _, out _, out var editLocks, out _, out _, out _, out var info);
                if (info != IntPtr.Zero) { Marshal.Release(info); }
                if (editLocks == 0) { return; }
                ErrorHandler.ThrowOnFailure(rdt.UnlockDocument((uint)_VSRDTFLAGS.RDT_EditLock, cookie));
                _log.Debug(() => $"[changes] let go of an edit lock Copilot left on its baseline copy {_tempPath}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"[changes] could not close Copilot's copy of the baseline of {Path}: {ex.Message}");
        }
    }

    // Copilot closes the second view its review is drawn in when the review ends, and keyboard focus goes with it when the person had
    // just pressed a button of the panel: the editor's commands (undo, redo) then reach no view until focus lands in one again, which
    // is what switching to another window and back does. Put in the document's own view, but only when it is nowhere: focus that is
    // in the chat, or in another window, is not taken.
    private void RestoreEditorFocus()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (View.IsClosed || !View.VisualElement.IsVisible) { return; }
            var focused = Keyboard.FocusedElement as DependencyObject;
            var lost = focused is null || (focused is Visual visual && PresentationSource.FromVisual(visual) is null);
            if (lost) { Keyboard.Focus(View.VisualElement); }
        }
        catch (Exception ex)
        {
            _log.Debug(() => $"[changes] could not give the editor of {Path} its focus back: {ex.Message}");
        }
    }

    // By default the editor's difference ignores white space at the ends of a line, and a line break is white space to it: a change that
    // adds or removes a last line then ends before its own line break, and Copilot's Undo, which writes the baseline's text over the
    // span the change has, leaves the break behind (a blank line where the line was). Lines as they are, for as long as the review is
    // on: the same way Copilot counts the changes itself, so the number it shows is the number of changes it draws. Said once the
    // difference buffer exists, which is a moment after the view is wired.
    private void CompareLinesExactly()
    {
        if (_differences is not null) { return; }
        try
        {
            var buffer = _window.DifferenceBuffer;
            if (buffer is null) { return; }
            buffer.Options.SetOptionValue(DifferenceBufferOptions.IgnoreWhiteSpaceBehaviorId, IgnoreWhiteSpaceBehavior.None);
            _differences = buffer;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _log.Warn($"[changes] could not make the difference of {Path} compare lines exactly: {ex.Message}. A change on a last line may leave its line break behind when it is undone");
        }
    }

    // The option was this review's, and the buffer it is on outlives the review.
    private void GiveBackDifferenceOptions()
    {
        var buffer = _differences;
        _differences = null;
        if (buffer is null) { return; }
        try { buffer.Options.ClearOptionValue(DifferenceBufferOptions.IgnoreWhiteSpaceBehaviorId); }
        catch (Exception ex) { _log.Debug(() => $"[changes] could not give back the white space option of {Path}: {ex.Message}"); }
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
        _document.TextBuffer.Changed -= OnDocumentChanged;
        if (_differences is not null) { _differences.SnapshotDifferenceChanged -= OnDifferenceWorkedOut; }
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
            // After Copilot's own clean-up: with the difference still up, they would redraw it.
            GiveBackDifferenceOptions();
            CloseBaselineDocument();
            if (!viewClosed)
            {
                RestoreEditorFocus();
                // A change undone that left no other ends the review here, with no dismissal: the document is the baseline, and the
                // last edit put it back. Copilot takes the difference down before it lets go of the state, so by now the document's
                // own view has the caret and the top of the view.
                if (_putBackStart is null && DocumentIsBaseline()) { NotePutBack(); }
                ShowPutBack(View);
            }
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
            GiveBackDifferenceOptions();
            CloseBaselineDocument();
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
        _log.Debug(() => $"[changes] {Path}: Copilot counts {changes} change(s)");
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
                if (string.Equals(text, _lastBaselineText, StringComparison.Ordinal))
                {
                    _log.Debug(() => $"[changes] {Path}: a change was undone in the editor, the baseline is as it was ({text.Length} characters)");
                    NotePutBack();
                    // Once the editor has worked the difference out on the new text: already, or in a moment (OnDifferenceWorkedOut).
                    if (DifferenceIncludesPutBack()) { ShowPutBack(ShownViewNow()); }
                    continue;
                }
                _log.Debug(() => $"[changes] {Path}: a change was kept in the editor, the baseline went from {_lastBaselineText.Length} to {text.Length} characters");
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
        _document.TextBuffer.Changed -= OnDocumentChanged;
        if (_differences is not null) { _differences.SnapshotDifferenceChanged -= OnDifferenceWorkedOut; }
        UnhookCopilot();
        // Copilot has ended it itself: nobody has to wait for this review any more, and Keep below asks for its end.
        _over.TrySetResult(true);
        try
        {
            await Task.Delay(SettleTime);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Its clean-up hides the difference and then lets go of the state, which deletes the temp copy: after that nothing of this
            // review is on the screen, and what it left is let go of.
            await WaitForAsync(() => !ReferenceEquals(_shape.TryGetState(Path), _state), 1500);
            GiveBackDifferenceOptions();
            CloseBaselineDocument();
            RestoreEditorFocus();

            var current = _document.TextBuffer.CurrentSnapshot.GetText();
            var undone = string.Equals(current, _lastBaselineText, StringComparison.Ordinal);
            _log.Info($"[changes] Copilot ended the review of {Path}: {(undone ? "undone, the document is the baseline" : "kept as it is")} "
                      + $"(the document has {current.Length} characters, the baseline {_lastBaselineText.Length}, the file on disk {SafeDiskLength()})");
            // Undo all wrote the baseline over the whole document. Copilot took the difference down before it let go of the state,
            // so the editor is done placing the caret and the top of the view.
            if (undone) { NotePutBack(); }
            ShowPutBack(View);

            // A file Copilot has left empty was not accepted by anybody: its earlier text is what the row's Undo puts back, so the row
            // stays, and is not forgotten as a Keep would.
            if (current.Length == 0 && _lastBaselineText.Length > 0)
            {
                _log.Warn($"[changes] {Path} is empty after Copilot ended its review: it is left in the list, where Undo puts the earlier text back");
                Say("This file is empty now. Undo in the Changes list puts back how it was before Claude's change.");
                await Owner.RemeasureAsync(Path);
                return;
            }
            if (undone) { await Owner.RemeasureAfterReviewAsync(Path); }
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
        // Claude wrote the file again while it was reviewed, and the editor took the new text from the disk: that, too, is not for
        // Ctrl+Z to take back. A turn later, once the editor has finished the reload it records.
        if ((e.FileActionType & FileActionTypes.ContentLoadedFromDisk) != 0)
        {
            if (ReloadChangedNothing())
            {
                _log.Debug(() => $"[changes] {Path}: reloaded from the disk with the text it had, Ctrl+Z still takes back what was done before");
            }
            else
            {
                Post(() =>
                {
                    if (_phase == Phase.Active) { DiscardUndoHistory("the file was reloaded from the disk"); }
                    return Task.CompletedTask;
                });
            }
        }
        if ((e.FileActionType & FileActionTypes.ContentSavedToDisk) == 0) { return; }
        _log.Debug(() => $"[changes] {Path}: saved, the document has {_document.TextBuffer.CurrentSnapshot.Length} characters");
        RequestMeasure();
    }

    // Copilot saves a change it undid through the document, a save the editor does not count as its own: it sees the file change
    // on the disk and reloads it, with the text it already has. Such a reload records nothing for Ctrl+Z, and clearing the history
    // after it took away the Undo itself, the one thing there was to take back. The editor tags its reload with the document, and
    // says the file was loaded right after it.
    private bool ReloadChangedNothing()
        => _lastEdit is { } reload && reload.EditTag is ITextDocument && reload.After == _document.TextBuffer.CurrentSnapshot
           && (reload.Changes.Count == 0 || string.Equals(reload.Before.GetText(), reload.After.GetText(), StringComparison.Ordinal));

    // Claude writes the file on disk, and the editor takes the new text into the open document as an edit of its own, which Ctrl+Z
    // takes back: all of Claude's change at once, in the editor only, under a review that is drawing against it. A person who pressed
    // Keep on one change and then Ctrl+Z to take the Keep back (a Keep is not an edit of the document, so there is nothing of it to
    // take back) got the other changes wiped out of the editor instead, and the file on disk still had them. So what Ctrl+Z could take
    // back from before the review is forgotten when it starts, and again whenever a reload during it brings new text. What is done
    // in the editor during the review (a change undone with Copilot's button, the person's own typing) stays undoable. The document
    // is never dirty here: a review only starts on a document that is what the disk holds.
    private void DiscardUndoHistory(string why)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var adapter = _registry.Adapters?.GetBufferAdapter(_document.TextBuffer);
            if (adapter is null || adapter.GetUndoManager(out var undo) != VSConstants.S_OK || undo is null) { return; }
            undo.DiscardFrom(null);
            _log.Debug(() => $"[changes] {Path}: Ctrl+Z no longer goes back before this point ({why})");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.Warn($"[changes] could not clear the undo history of {Path}: {ex.Message}. Ctrl+Z may take back Claude's change at once");
        }
    }

    /// <summary>Writes down a command run on this document while it is reviewed (see <see cref="ReviewRegistry"/>), with the line
    /// the caret is on and what Ctrl+Z and Redo have to work on: on a line of a change Ctrl+Y is Copilot's Keep, not Redo.</summary>
    internal void NoteCommand(string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_phase != Phase.Active) { return; }
        _log.Debug(() =>
        {
            var view = ShownViewNow();
            return $"[changes] {Path}: {name}, the caret on line {DocumentLine(view, view.Caret.Position.BufferPosition)}; {UndoState()}";
        });
    }

    // As the shell's undo manager of the document counts them, which is what Ctrl+Z and Redo act on.
    private string UndoState()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var adapter = _registry.Adapters?.GetBufferAdapter(_document.TextBuffer);
            if (adapter is null || adapter.GetUndoManager(out var undo) != VSConstants.S_OK || undo is null) { return "no undo manager"; }
            undo.EnumUndoable(out var undoable);
            undo.EnumRedoable(out var redoable);
            var steps = Count(undoable);
            var last = "";
            if (steps > 0)
            {
                undo.GetLastUndoDescription(out var text);
                last = $" (the last: {text})";
            }
            return $"{steps} step(s) for Ctrl+Z{last}, {Count(redoable)} for Redo";
        }
        catch (COMException ex)
        {
            return $"the undo manager could not be read: {ex.Message}";
        }
    }

    private static int Count(IEnumOleUndoUnits units)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (units is null) { return 0; }
        var count = 0;
        var one = new IOleUndoUnit[1];
        while (units.Next(1, one, out var fetched) == VSConstants.S_OK && fetched == 1) { count++; }
        return count;
    }

    // Every change to the document while the review is on is written down, with its size: whatever Copilot's buttons did to the file
    // is then in the log, not a guess. A document that comes out empty is a loss nobody asked for (Undo writes the baseline, which
    // is not empty): what it held is kept first, so that it can be got back, and said.
    private void OnDocumentChanged(object sender, TextContentChangedEventArgs e)
    {
        _lastEdit = e;
        var before = e.Before.Length;
        var after = e.After.Length;
        _log.Debug(() => $"[changes] {Path}: the document changed, {before} to {after} characters, {e.Changes.Count} edit(s)");
        if (after == 0 && before > 0) { _ = KeepRecoveryCopyAsync(e.Before); }
    }

    // Not awaited by the event: the copy is written off the editor's own dispatch, and says so when it is done.
    private async Task KeepRecoveryCopyAsync(ITextSnapshot before)
    {
        try
        {
            var file = System.IO.Path.Combine(AppPaths.RecoveryFolder, $"{System.IO.Path.GetFileName(Path)}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            // The folder, and the old copies, have no async form: the pool.
            await Task.Run(() => Directory.CreateDirectory(AppPaths.RecoveryFolder)).ConfigureAwait(false);
            await AsyncFile.WriteAllTextAsync(file, before.GetText(), new UTF8Encoding(false)).ConfigureAwait(false);
            await Task.Run(TrimRecoveryFolder).ConfigureAwait(false);
            _log.Warn($"[changes] {Path} was emptied while its review was on: what it held before ({before.Length} characters) is kept in {file}");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Say($"{System.IO.Path.GetFileName(Path)} was emptied: its text before is kept in {file}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"[changes] {Path} was emptied while its review was on, and what it held could not be kept: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.LogException("CopilotReview.KeepRecoveryCopy", ex);
        }
    }

    private static void TrimRecoveryFolder()
    {
        foreach (var old in new DirectoryInfo(AppPaths.RecoveryFolder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
        {
            try { old.Delete(); }
            catch (IOException) { /* an old copy that is in use stays */ }
        }
    }

    private string SafeDiskLength()
    {
        try { return new FileInfo(Path).Length.ToString(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unknown"; }
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
            // The last change kept or undone, or the file saved as it was: the person has finished with the file here.
            await Owner.RemeasureAfterReviewAsync(Path);
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

    // The view on the screen now: the difference's while it is up, the document's own once it is taken down.
    private IWpfTextView ShownViewNow()
        => _window.Baseline is not null && (View.TextViewModel as IDifferenceTextViewModel)?.Viewer is { } viewer ? ShownView(viewer) : View;

    // What the last edit put back: from where its old and new text first differ (Undo all replaces the document from its first
    // character, wherever the change is) to the end of what it wrote.
    private void NotePutBack()
    {
        var edit = _lastEdit;
        if (edit is null || edit.Changes.Count == 0) { return; }
        var first = edit.Changes[0];
        var start = first.NewPosition + CommonPrefixLength(first.OldText, first.NewText);
        _putBackStart = edit.After.CreateTrackingPoint(start, PointTrackingMode.Negative);
        _putBackEnd = edit.After.CreateTrackingPoint(edit.Changes[edit.Changes.Count - 1].NewEnd, PointTrackingMode.Positive);
        _putBackVersion = edit.After.Version.VersionNumber;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < length && a[i] == b[i]) { i++; }
        return i;
    }

    private bool DocumentIsBaseline()
        => string.Equals(_document.TextBuffer.CurrentSnapshot.GetText(), _lastBaselineText, StringComparison.Ordinal);

    // Whether the editor's difference has been worked out on the document since the put back: its viewer has moved the caret and
    // the view by then.
    private bool DifferenceIncludesPutBack()
    {
        var right = _differences?.CurrentSnapshotDifference?.RightBufferSnapshot;
        return right is not null && right.TextBuffer == _document.TextBuffer && right.Version.VersionNumber >= _putBackVersion;
    }

    // The editor has worked the difference out again, and its viewer has put the caret and the top of the view back where it
    // carries them: past the text an Undo put back, maybe. Handled a turn later, once every handler of this event has run, the
    // viewer's among them.
    private void OnDifferenceWorkedOut(object sender, SnapshotDifferenceChangeEventArgs e)
    {
        if (_putBackStart is null || !DifferenceIncludesPutBack()) { return; }
        Post(() =>
        {
            if (_phase == Phase.Active) { ShowPutBack(ShownViewNow()); }
            return Task.CompletedTask;
        });
    }

    // Copilot's Undo writes the earlier text over the whole of a change in one edit (Undo all: over the whole document), and the
    // editor carries the caret and the top of the view through it to its end: its difference viewer translates both with positive
    // tracking when it works the difference out again, and hands them to the document's own view when the difference is taken
    // down. For a change that ran to the end of the file, the window then shows the last line alone, with the text put back above
    // it and off the screen: a file that looks emptied. (Copilot scrolls to the top after Undo all, but in the document's view while
    // the inline one is on the screen, and before the editor moves them.) Once the editor is done, the caret goes to where the put
    // back text starts if it was pushed to its end, and the view shows that line if it is not on the screen.
    private void ShowPutBack(IWpfTextView view)
    {
        var startPoint = _putBackStart;
        var endPoint = _putBackEnd;
        _putBackStart = _putBackEnd = null;
        if (startPoint is null || view is null || view.IsClosed) { return; }
        try
        {
            var snapshot = _document.TextBuffer.CurrentSnapshot;
            var start = startPoint.GetPoint(snapshot).GetContainingLine();
            var shown = view.BufferGraph.MapUpToSnapshot(start.Start, PointTrackingMode.Negative, PositionAffinity.Successor, view.TextSnapshot);
            if (shown is null) { return; }

            var caret = view.Caret.Position.Point.GetPoint(_document.TextBuffer, PositionAffinity.Successor);
            var pushed = caret is not null && caret.Value.Position == endPoint.GetPosition(snapshot);
            var line = view.TextViewLines?.GetTextViewLineContainingBufferPosition(shown.Value);
            var onScreen = line is not null && line.VisibilityState == VisibilityState.FullyVisible;
            var top = view.TextViewLines?.FirstVisibleLine;
            var left = $"the caret on line {(caret is null ? "-" : (caret.Value.GetContainingLine().LineNumber + 1).ToString())}, "
                       + $"line {(top is null ? "-" : DocumentLine(view, top.Start).ToString())} at the top";
            if (onScreen && !pushed)
            {
                _log.Debug(() => $"[changes] {Path}: Copilot's Undo put text back from line {start.LineNumber + 1}, which is on the screen ({left})");
                return;
            }
            view.Selection.Clear();
            view.Caret.MoveTo(shown.Value);
            if (!onScreen) { view.ViewScroller.EnsureSpanVisible(new SnapshotSpan(shown.Value, 0), EnsureSpanVisibleOptions.AlwaysCenter); }
            _log.Debug(() => $"[changes] {Path}: Copilot's Undo put text back from line {start.LineNumber + 1}, and the editor left {left}: moved there");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log.Debug(() => $"[changes] could not show where Copilot's Undo put text back in {Path}: {ex.Message}");
        }
    }

    // The document's line a point of a view is on, from 1; 0 for one that is not the document's (a deleted line of the inline view).
    private int DocumentLine(IWpfTextView view, SnapshotPoint point)
    {
        var mapped = view.BufferGraph.MapDownToBuffer(point, PointTrackingMode.Negative, _document.TextBuffer, PositionAffinity.Successor);
        return mapped is null ? 0 : mapped.Value.GetContainingLine().LineNumber + 1;
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
    private async Task<bool?> BufferMatchesDiskAsync()
    {
        byte[] bytes;
        try { bytes = await AsyncFile.ReadAllBytesAsync(Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(() => $"[changes] could not read {Path} to compare it with the editor: {ex.Message}");
            return null;
        }
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

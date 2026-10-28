/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>Which listed files are being reviewed in their own editor, process-wide: every chat pane's list is fed in, and each
/// file that is open, and can be shown, gets a <see cref="CopilotReview"/>: GitHub Copilot's own review UI, against the baseline
/// of its row. Without Copilot nothing is drawn, and Open shows the comparison window.
/// <para>It works by reconciliation rather than by events about single files: whenever any list changes, or a document
/// window is about to be shown, <see cref="SyncAsync"/> compares what every pane lists with what is being reviewed, ends the
/// reviews that have lost their row and starts the ones that have gained a file. So a Keep or an Undo in the panel, which
/// only touch the list, end the review without anything having to tell it. A pass is always a turn of the dispatcher of its
/// own and never runs inside another: it edits rows, which asks for a pass.</para>
/// <para>No MEF of our own: documents are found through the running document table, as the comparison window is, and Copilot's
/// parts are asked for by name (<see cref="CopilotReviews"/>).</para>
/// <para>A file two panes list is reviewed for one of them (<see cref="ReviewOwnership"/>), and both rows say so
/// (<see cref="FileChange.SharedWithPane"/>): the person is told in the panel, not left to find out in the log.</para>
/// All of it runs on the UI thread.</summary>
internal sealed class ReviewRegistry : IVsRunningDocTableEvents
{
    // A file whose editor has not caught up with the disk is tried again a few times, a second apart, and then left for the
    // event that says the editor has: a save, or a reload.
    private const int MaxNotYet = 6;

    public static ReviewRegistry Instance { get; } = new();

    private readonly OutputWindowLogger _log = OutputWindowLogger.Global;
    private readonly List<SessionChanges> _owners = new();
    private readonly Dictionary<SessionChanges, Action> _subscriptions = new();
    private readonly Dictionary<string, CopilotReview> _reviews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Declined> _declined = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Claim> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _notYet = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _attaching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IWpfTextView, LoadWait> _waitingForLoad = new();

    // How many reviews there are, readable from any thread: Keep and Undo ask to end a file's review from wherever they run, and
    // when nothing is reviewed (no Copilot, or a test) that is answered without the UI thread.
    private int _reviewCount;

    private int _syncQueued;
    private bool _syncRunning;
    private bool _syncAgain;
    private bool _servicesTried;
    private IVsEditorAdaptersFactoryService _adapters;

    /// <summary>The editor's adapters, for a review that has to reach the shell's side of its document. Null before the first pass.</summary>
    internal IVsEditorAdaptersFactoryService Adapters => _adapters;
    private ITextDocumentFactoryService _documents;
    private IVsRunningDocumentTable _rdt;
    private uint _rdtCookie;

    // What is pressed in a reviewed document goes into the log by name: Ctrl+Z, Copilot's buttons and its keys are all "undo" in
    // a report, and Ctrl+Y is Copilot's Keep on a line of a change and Redo anywhere else. A click on the Keep or Undo drawn by a
    // change is not a command and shows only as what it did. DTE stops raising the events of a sink nothing holds.
    private static readonly (string Guid, int Id, string Name)[] WatchedCommands =
    {
        ("{5EFC7975-14BC-11CF-9B2B-00AA00573819}", 43, "Edit.Undo"),
        ("{5EFC7975-14BC-11CF-9B2B-00AA00573819}", 29, "Edit.Redo"),
        ("{39B0DEDE-D931-4A92-9AA2-3447BC4998DC}", 2048, "Copilot.KeepAll"),
        ("{39B0DEDE-D931-4A92-9AA2-3447BC4998DC}", 2049, "Copilot.UndoAll"),
        ("{39B0DEDE-D931-4A92-9AA2-3447BC4998DC}", 2050, "Copilot.KeepCurrentChange"),
        ("{39B0DEDE-D931-4A92-9AA2-3447BC4998DC}", 2051, "Copilot.UndoCurrentChange"),
    };
    private readonly List<EnvDTE.CommandEvents> _watchedCommands = new();

    /// <summary>A file that could not be shown, as it was when that was found out: another row of it, or the same row measured
    /// again after the file changed, is another chance.</summary>
    private sealed class Declined
    {
        public SessionChanges Owner;
        public long Serial;
        public long StampLength;
        public DateTime StampWriteUtc;
    }

    /// <summary>A pane that was asked to show a file (the person pressed Open in it).</summary>
    private sealed class Claim
    {
        public Claim(SessionChanges owner, DateTime atUtc)
        {
            Owner = owner;
            AtUtc = atUtc;
        }

        public SessionChanges Owner { get; }
        public DateTime AtUtc { get; }
    }

    private sealed class LoadWait
    {
        public RoutedEventHandler Loaded;
        public EventHandler Closed;
    }

    private readonly struct Listing
    {
        public Listing(SessionChanges owner, FileChange row)
        {
            Owner = owner;
            Row = row;
        }

        public SessionChanges Owner { get; }
        public FileChange Row { get; }
    }

    /// <summary>What an attempt to review a file came to.</summary>
    private enum Attach
    {
        /// <summary>The review is on.</summary>
        Reviewing,

        /// <summary>The file is not open in a window that is ready: the window events try again.</summary>
        Waiting,

        /// <summary>The editor and the disk do not agree yet, or the document has unsaved changes.</summary>
        NotYet,

        /// <summary>This file is not reviewed, and the log says why.</summary>
        Declined,
    }

    private sealed class Parts
    {
        public IWpfTextView View;
        public ITextDocument Document;
        public IVsDifferenceCodeWindow2 Window;
    }

    /// <summary>Start following a pane's list. Called once, when the pane makes it.</summary>
    public void Register(SessionChanges owner)
    {
        if (!ThreadHelper.CheckAccess())
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Register(owner);
            }).FileAndForget("ReviewRegistry.Register");
            return;
        }
        if (owner is null || _owners.Contains(owner)) { return; }
        _owners.Add(owner);
        Action changed = () => ScheduleSync();
        _subscriptions[owner] = changed;
        owner.Set.Changed += changed;
        ScheduleSync();
    }

    /// <summary>Stop following a pane's list, and end the reviews it started.</summary>
    public void Unregister(SessionChanges owner)
    {
        if (!ThreadHelper.CheckAccess())
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Unregister(owner);
            }).FileAndForget("ReviewRegistry.Unregister");
            return;
        }
        if (owner is null || !_owners.Remove(owner)) { return; }
        if (_subscriptions.TryGetValue(owner, out var changed))
        {
            owner.Set.Changed -= changed;
            _subscriptions.Remove(owner);
        }
        foreach (var review in _reviews.Values.Where(r => ReferenceEquals(r.Owner, owner)).ToList())
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(() => review.EndAsync()).FileAndForget("ReviewRegistry.Unregister.End");
        }
        ScheduleSync();
    }

    /// <summary>The review that is on for the document the person is in, if any: for the buttons Copilot's panel carries for us,
    /// which act on the file whose window it is.</summary>
    public CopilotReview ReviewForActiveDocument()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_reviews.Count == 0) { return null; }
        if (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection monitor
            || monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var frameObject) != VSConstants.S_OK
            || frameObject is not IVsWindowFrame frame)
        {
            return null;
        }
        var path = IdeContextService.DocumentMoniker(frame);
        if (path is null) { return null; }
        var key = IdeContextService.FrameKey(path);
        return _reviews.Values.FirstOrDefault(r => r.IsActive
                                                   && string.Equals(IdeContextService.FrameKey(r.Path), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Something a review did may have changed what the lists say about it: look again.</summary>
    public void Poke() => ScheduleSync();

    /// <summary>End the review of a file, if there is one, and return when Copilot has let go of it. For Keep and Undo, which are
    /// about to act on the file itself.</summary>
    public async Task EndAsync(string path)
    {
        if (Volatile.Read(ref _reviewCount) == 0) { return; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var key = IdeContextService.FrameKey(path);
        var review = _reviews.Values.FirstOrDefault(r => string.Equals(IdeContextService.FrameKey(r.Path), key, StringComparison.OrdinalIgnoreCase));
        if (review is not null) { await review.EndAsync(); }
    }

    /// <summary>A review ended. When its view was closed under it (the pane of a split that was removed, say) and the file is
    /// still listed and open, the file gets another review in what is left of the window, a moment later: by then a tab the
    /// person closed is gone from the frames, and nothing is started for it.</summary>
    internal void Detached(CopilotReview review, bool viewClosed)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_reviews.TryGetValue(review.Path, out var held) && ReferenceEquals(held, review)) { Untrack(review.Path); }
        RefreshCommands();
        if (viewClosed) { ScheduleSync(400); }
        else { ScheduleSync(); }
    }

    /// <summary>Show a listed file in its own editor with Copilot's review drawn in it, for the pane whose list is
    /// <paramref name="set"/>, opening the file if it is not open. False, with nothing done to the editor, when it cannot be
    /// shown there: the caller then opens the comparison window.</summary>
    public async Task<bool> ShowAsync(ChangeSet set, string path)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        SessionChanges owner = null;
        var shown = false;
        try
        {
            if (!AgentsOptions.Chat.ReviewInEditor) { return false; }
            EnsureServices();
            if (_adapters is null || CopilotReviews.Shape(_log) is null) { return false; }
            owner = _owners.FirstOrDefault(o => ReferenceEquals(o.Set, set));
            if (owner is null || !set.TryGet(path, out var row)) { return false; }

            // The person asked for this file in this chat: it is this chat's to show, until another chat changes it later.
            _claims[path] = new Claim(owner, DateTime.UtcNow);
            _declined.Remove(path);
            _notYet.Remove(path);

            // The editor's view is made a moment after the file is opened, and has to be loaded before it can be switched; the
            // review itself takes a moment to be drawn. The row and whoever holds the file are looked at again each time round:
            // a pass of the registry runs in between.
            var opened = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (!set.TryGet(path, out row)) { return false; }
                if (_reviews.TryGetValue(path, out var held))
                {
                    if (ReferenceEquals(held.Owner, owner) && held.RowSerial == row.Serial)
                    {
                        if (held.IsActive)
                        {
                            held.Reveal();
                            shown = true;
                            return true;
                        }
                        // Starting, or being taken down: a moment.
                        await Task.Delay(50);
                        continue;
                    }
                    // Another chat's review of the file, or this chat's of a row that has gone: the file is this chat's to show now.
                    await held.EndAsync();
                    continue;
                }

                if (!opened)
                {
                    if (!await IdeContextService.Instance.OpenFileAsync(path, 0, 0, true)) { return false; }
                    opened = true;
                }

                var frames = IdeContextService.FindDocumentFrames(path);
                if (frames.Count > 0)
                {
                    var outcome = await TryAttachAsync(owner, row, frames, fromOpen: true);
                    // Not ready, and waiting does not make it so within the time a click can wait: the comparison window is the answer.
                    if (outcome == Attach.NotYet)
                    {
                        _log.Info($"[changes] {path} is shown in the comparison window: the editor has not caught up with the file, or it has unsaved changes");
                        return false;
                    }
                }
                if (_reviews.ContainsKey(path)) { continue; }
                if (_declined.ContainsKey(path)) { return false; }
                await Task.Delay(50);
            }
            _log.Info($"[changes] {path} did not become ready to show in place: the comparison window is used");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("ReviewRegistry.ShowAsync", ex);
            return false;
        }
        finally
        {
            // A claim that came to nothing must not keep this chat the winner of a file it is not showing: the chat whose review
            // was taken down for it gets it back.
            if (!shown && owner is not null && _claims.TryGetValue(path, out var claim) && ReferenceEquals(claim.Owner, owner))
            {
                _claims.Remove(path);
                ScheduleSync();
            }
        }
    }

    // The buttons this extension puts in Copilot's panel show only for a file that is reviewed, in a list of more than one: the shell
    // asks again when something in the interface tells it to (a click, a change of window), not when a review starts or the list
    // changes while the person is in the chat, so it is told.
    private static void RefreshCommands()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell) { shell.UpdateCommandUI(0); }
        }
        catch (Exception)
        {
            // A courtesy: the buttons are right at the next click.
        }
    }

    private void Track(string path, CopilotReview review)
    {
        _reviews[path] = review;
        Volatile.Write(ref _reviewCount, _reviews.Count);
    }

    private void Untrack(string path)
    {
        _reviews.Remove(path);
        Volatile.Write(ref _reviewCount, _reviews.Count);
    }

    /// <summary>Queue a reconciliation on the UI thread, after <paramref name="delayMs"/>. One that is already waiting covers
    /// this one: the lists change in bursts, and a pass looks at everything.</summary>
    private void ScheduleSync(int delayMs = 0)
    {
        if (delayMs == 0 && Interlocked.Exchange(ref _syncQueued, 1) == 1) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            if (delayMs > 0) { await Task.Delay(delayMs); }
            // Always a later turn of the dispatcher, never inside the call that asked, or the row a pass edits would start a
            // pass inside it, and that one a third.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            if (delayMs == 0) { Interlocked.Exchange(ref _syncQueued, 0); }
            await SyncAsync();
        }).FileAndForget("ReviewRegistry.Sync");
    }

    // One pass at a time: a pass waits for a file to be read, and one asked for meanwhile makes the running one go round again
    // instead of running beside it.
    private async Task SyncAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_syncRunning)
        {
            _syncAgain = true;
            return;
        }
        _syncRunning = true;
        try
        {
            do
            {
                _syncAgain = false;
                try { await SyncOnceAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A request that came in during the pass that failed is still owed its pass.
                    _log.LogException("ReviewRegistry.SyncAsync", ex);
                }
            }
            while (_syncAgain);
        }
        finally
        {
            _syncRunning = false;
        }
    }

    private async Task SyncOnceAsync()
    {
        EnsureServices();
        var enabled = AgentsOptions.Chat.ReviewInEditor;

        var listed = new Dictionary<string, List<Listing>>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in _owners.ToList())
        {
            foreach (var row in owner.Set.Snapshot())
            {
                if (!listed.TryGetValue(row.Path, out var rows)) { listed[row.Path] = rows = new List<Listing>(); }
                rows.Add(new Listing(owner, row));
            }
        }

        // Whose review a file gets, and what each pane's row says of the other: the rule is ReviewOwnership's.
        var winners = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in listed)
        {
            var listings = pair.Value;
            var candidates = listings.Select(l => new ReviewOwnership.Candidate<SessionChanges>(l.Owner, l.Row.LastChangedUtc, l.Owner.PaneNumber)).ToList();
            _reviews.TryGetValue(pair.Key, out var held);
            _claims.TryGetValue(pair.Key, out var claim);
            var winner = ReviewOwnership.Winner(candidates, held?.Owner, claim?.Owner, claim?.AtUtc ?? default);
            winners[pair.Key] = listings[winner];

            // Only a row that has to change is touched: a touch is a change, and a change brings another pass.
            var marks = ReviewOwnership.SharedWith(candidates, winner);
            for (var i = 0; i < listings.Count; i++)
            {
                var mark = marks[i];
                if (listings[i].Row.SharedWithPane == mark) { continue; }
                listings[i].Owner.Set.Update(pair.Key, r => r.SharedWithPane = mark);
            }
        }

        // What is remembered about a file the panes no longer list, or that this pane no longer lists, goes.
        foreach (var path in _claims.Keys.ToList())
        {
            var claimed = _claims[path].Owner;
            if (!listed.TryGetValue(path, out var rows) || !rows.Any(l => ReferenceEquals(l.Owner, claimed))) { _claims.Remove(path); }
        }
        foreach (var path in _declined.Keys.ToList())
        {
            var declined = _declined[path];
            if (!listed.TryGetValue(path, out var rows) || !rows.Any(l => ReferenceEquals(l.Owner, declined.Owner) && l.Row.Serial == declined.Serial))
            {
                _declined.Remove(path);
            }
        }
        foreach (var path in _notYet.Keys.ToList())
        {
            if (!listed.ContainsKey(path)) { _notYet.Remove(path); }
        }

        // A review whose row is gone (kept, undone, back to how it was), or has been replaced, or whose pane lost the file to
        // another, ends: nothing here tells it, the list is the only thing that changed. One that is still starting or already
        // being taken down is not touched: it is somebody's already.
        foreach (var review in _reviews.Values.ToList())
        {
            if (!review.IsActive || review.IsReplacingBaseline) { continue; }
            if (!enabled
                || !winners.TryGetValue(review.Path, out var winner)
                || !ReferenceEquals(winner.Owner, review.Owner)
                || winner.Row.Serial != review.RowSerial)
            {
                await review.EndAsync();
            }
        }

        RefreshCommands();
        if (!enabled || _adapters is null) { return; }
        // Only a file that is open can be shown: one walk over the frames, not one per row.
        var open = IdeContextService.OpenDocumentFrames();
        foreach (var winner in winners.Values.ToList())
        {
            if (_reviews.ContainsKey(winner.Row.Path)) { continue; }
            if (!open.TryGetValue(IdeContextService.FrameKey(winner.Row.Path), out var frames)) { continue; }
            try { await TryAttachAsync(winner.Owner, winner.Row, frames, fromOpen: false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogException("ReviewRegistry.TryAttach", ex);
            }
        }
    }

    /// <summary>Review a file in place if it is open and can be. A file that is not open is left for the window event that
    /// comes when it is; one that cannot be shown is declined, once, with the reason in the log.</summary>
    private async Task<Attach> TryAttachAsync(SessionChanges owner, FileChange row, IReadOnlyList<IVsWindowFrame> frames, bool fromOpen)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = row.Path;
        if (!_attaching.Add(path)) { return Attach.Waiting; }
        try
        {
            if (_reviews.ContainsKey(path)) { return Attach.Reviewing; }
            if (IsDeclined(owner, row)) { return Attach.Declined; }

            // Said once, by the probe: without Copilot there is nothing to draw the review with.
            var shape = CopilotReviews.Shape(_log);
            if (shape is null) { return Attach.Declined; }

            // The frame that has the code editor: a file in two windows, or a designer with its code view, has more than one.
            Parts parts = null;
            IVsWindowFrame frame = null;
            string why = null;
            foreach (var candidate in frames)
            {
                Parts found;
                string reason;
                try { found = Inspect(candidate, out reason, out _); }
                catch (COMException ex)
                {
                    // A window that is still being made has no code window view yet, and says so with a failure.
                    _log.Debug(() => $"[changes] the window of {path} cannot be inspected yet: {ex.Message}");
                    continue;
                }
                if (found is not null)
                {
                    parts = found;
                    frame = candidate;
                    break;
                }
                why ??= reason;
            }
            if (parts is null)
            {
                // No reason is "not yet": the view of a document that has only just been opened does not exist for a moment.
                if (why is null) { return Attach.Waiting; }
                Decline(owner, row, why);
                return Attach.Declined;
            }
            if (!parts.View.VisualElement.IsLoaded)
            {
                WaitForLoad(parts.View);
                return Attach.Waiting;
            }

            var (problem, baseline) = await BaselineReview.PrepareAsync(row);
            if (problem is not null)
            {
                Decline(owner, row, problem);
                return Attach.Declined;
            }
            // A file Claude created has no earlier text: Copilot's review draws a difference, and its Undo all would leave an
            // empty file where the list's Undo deletes it. The list's own Keep and Undo take such a file whole.
            if (baseline.IsEmpty)
            {
                Decline(owner, row, "the file is new, so there is no earlier text to show a difference against (the list's Keep and Undo take it whole)");
                return Attach.Declined;
            }

            // Things may have moved while the baseline was read.
            if (_reviews.ContainsKey(path) || !_owners.Contains(owner) || !IdeContextService.FindDocumentFrames(path).Contains(frame)) { return Attach.Waiting; }
            if (!owner.Set.TryGet(path, out var now) || now.Serial != row.Serial) { return Attach.Waiting; }

            var review = new CopilotReview(this, owner, now, frame, parts.View, parts.Document, parts.Window, baseline, shape);
            Track(path, review);
            var (result, reason2) = await review.StartAsync();
            // Taken down by its own end while it started (the view was closed): the registry has been told already.
            if (_reviews.TryGetValue(path, out var held) && ReferenceEquals(held, review) && result != ReviewStart.Started) { Untrack(path); }

            switch (result)
            {
                case ReviewStart.Started:
                    _declined.Remove(path);
                    _notYet.Remove(path);
                    RefreshCommands();
                    // The row may have gone while it started: a pass ends what has lost its row.
                    ScheduleSync();
                    return Attach.Reviewing;

                case ReviewStart.NotYet:
                    NotYet(path, reason2, fromOpen);
                    return Attach.NotYet;

                default:
                    if (reason2 is not null) { Decline(owner, now, reason2); }
                    return reason2 is not null ? Attach.Declined : Attach.Waiting;
            }
        }
        finally
        {
            _attaching.Remove(path);
        }
    }

    // The editor and the disk do not agree yet, or the document has changes of the person's that are not saved. Tried again a few
    // times; after that the events of the editor do it (a save, a reload).
    private void NotYet(string path, string why, bool fromOpen)
    {
        _notYet.TryGetValue(path, out var misses);
        _notYet[path] = ++misses;
        _log.Debug(() => $"[changes] {path} is not ready to be reviewed yet ({why}), try {misses}");
        if (misses == MaxNotYet && !fromOpen)
        {
            _log.Info($"[changes] {path} is not reviewed in the editor for now: {why}. It is tried again when the editor saves or reloads it");
        }
        if (misses < MaxNotYet) { ScheduleSync(1000); }
    }

    /// <summary>What the frame's editor is made of, or null with <paramref name="why"/>. <paramref name="pending"/> says the
    /// answer is "not yet", and there is then no reason.</summary>
    private Parts Inspect(IVsWindowFrame frame, out string why, out bool pending)
    {
        why = null;
        pending = false;
        var vsView = VsShellUtilities.GetTextView(frame);
        var view = vsView is null ? null : _adapters.GetWpfTextView(vsView);
        if (view is null || view.IsClosed)
        {
            pending = true;
            return null;
        }
        if (!_documents.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var document))
        {
            why = "it has no text document";
            return null;
        }
        if (!ReferenceEquals(view.TextBuffer, document.TextBuffer))
        {
            why = "it is a projection (a file with another language embedded in it), which the difference view does not handle";
            return null;
        }
        if (view.Options.GetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId))
        {
            why = "it is read-only";
            return null;
        }
        // Copilot's review wires itself to a view of this kind: every document of this Visual Studio's editor has one.
        if (view.TextViewModel is not IDifferenceTextViewModel)
        {
            why = "its view is not one the editor can show a difference in";
            return null;
        }

        view.Properties.TryGetProperty(typeof(IVsCodeWindow), out IVsCodeWindow codeWindow);
        if (codeWindow is null && frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView) == VSConstants.S_OK)
        {
            codeWindow = docView as IVsCodeWindow;
        }
        if (codeWindow is not IVsDifferenceCodeWindow2 window)
        {
            why = "its window cannot show a difference";
            return null;
        }
        if (!window.SupportsDiff)
        {
            why = "this Visual Studio's editor cannot show a difference in it (SupportsDiff is false)";
            return null;
        }
        if (window.Baseline is not null)
        {
            why = "a difference is already shown in it (by Copilot's own agent, or a Git diff)";
            return null;
        }
        return new Parts { View = view, Document = document, Window = window };
    }

    private bool IsDeclined(SessionChanges owner, FileChange row)
        => _declined.TryGetValue(row.Path, out var known)
           && ReferenceEquals(known.Owner, owner) && known.Serial == row.Serial
           && known.StampLength == row.StampLength && known.StampWriteUtc == row.StampWriteUtc;

    private void Decline(SessionChanges owner, FileChange row, string why)
    {
        if (IsDeclined(owner, row)) { return; }
        _declined[row.Path] = new Declined { Owner = owner, Serial = row.Serial, StampLength = row.StampLength, StampWriteUtc = row.StampWriteUtc };
        _log.Info($"[changes] {row.Path} is not reviewed in the editor: {why}");
    }

    // The view of a document that has only just opened is not loaded yet, and the editor will not take a baseline until it is.
    // A view that is closed before that is not waited for any more: the registry outlives every view it has ever seen.
    private void WaitForLoad(IWpfTextView view)
    {
        if (_waitingForLoad.ContainsKey(view)) { return; }
        var wait = new LoadWait();
        wait.Loaded = (_, _) =>
        {
            StopWaiting(view);
            ScheduleSync();
        };
        wait.Closed = (_, _) => StopWaiting(view);
        _waitingForLoad[view] = wait;
        view.VisualElement.Loaded += wait.Loaded;
        view.Closed += wait.Closed;
    }

    private void StopWaiting(IWpfTextView view)
    {
        if (!_waitingForLoad.TryGetValue(view, out var wait)) { return; }
        _waitingForLoad.Remove(view);
        view.VisualElement.Loaded -= wait.Loaded;
        view.Closed -= wait.Closed;
    }

    private void EnsureServices()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_servicesTried) { return; }
        _servicesTried = true;
        try
        {
            var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            _adapters = components?.GetService<IVsEditorAdaptersFactoryService>();
            _documents = components?.GetService<ITextDocumentFactoryService>();
            if (_adapters is null || _documents is null)
            {
                _adapters = null;
                _log.Warn("[changes] the editor services are not available: files are not reviewed in the editor");
                return;
            }
            if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt)
            {
                _rdt = rdt;
                rdt.AdviseRunningDocTableEvents(this, out _rdtCookie);
            }
            // The setting applies at once, not at the next change of a list.
            AgentsOptions.Applied += () => ScheduleSync();
            WatchCommands();
        }
        catch (Exception ex)
        {
            _adapters = null;
            _log.LogException("ReviewRegistry.EnsureServices", ex);
        }
    }

    private void WatchCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is not EnvDTE.DTE dte) { return; }
        foreach (var (guid, id, name) in WatchedCommands)
        {
            try
            {
                var events = dte.Events.get_CommandEvents(guid, id);
                events.BeforeExecute += (string _, int _, object _, object _, ref bool _) => OnCommand(dte, name);
                _watchedCommands.Add(events);
            }
            catch (Exception ex) when (ex is COMException or ArgumentException)
            {
                _log.Debug(() => $"[changes] {name} run on a reviewed document is not logged: {ex.Message}");
            }
        }
    }

    private void OnCommand(EnvDTE.DTE dte, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_reviewCount == 0) { return; }
        try
        {
            var path = dte.ActiveDocument?.FullName;
            if (path is not null && _reviews.TryGetValue(path, out var review)) { review.NoteCommand(name); }
        }
        catch (COMException ex)
        {
            _log.Debug(() => $"[changes] could not tell which document {name} ran on: {ex.Message}");
        }
    }

    private string DocumentPath(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_rdt is null) { return null; }
        _rdt.GetDocumentInfo(docCookie, out _, out _, out _, out var moniker, out _, out _, out var docData);
        // The table hands the document's data over with a reference of its own.
        if (docData != IntPtr.Zero) { Marshal.Release(docData); }
        return moniker;
    }

    private bool IsListed(string path) => path is not null && _owners.Any(o => o.Set.TryGet(path, out _));

    // A window is about to be shown: if it is a listed file, it is a candidate. The view and its code window may not exist
    // yet on a first show, so the pass is repeated while they come up.
    int IVsRunningDocTableEvents.OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        try
        {
            var path = pFrame is null ? null : IdeContextService.DocumentMoniker(pFrame);
            if (IsListed(path))
            {
                _notYet.Remove(path);
                ScheduleSync();
                ScheduleSync(400);
                ScheduleSync(1500);
            }
        }
        catch (Exception ex)
        {
            _log.LogException("ReviewRegistry.OnBeforeDocumentWindowShow", ex);
        }
        return VSConstants.S_OK;
    }

    // A document with changes of the person's that were not saved, and so was not reviewed, is saved: it is what the disk has now,
    // and the list has not measured it. The save is what the review of a file that is on waits for as well, in its own handler.
    int IVsRunningDocTableEvents.OnAfterSave(uint docCookie)
    {
        try
        {
            var path = DocumentPath(docCookie);
            if (IsListed(path) && !_reviews.ContainsKey(path))
            {
                _notYet.Remove(path);
                foreach (var owner in _owners.ToList())
                {
                    if (owner.Set.TryGet(path, out _)) { owner.RemeasureAsync(path).FileAndForget("ReviewRegistry.Remeasure"); }
                }
                ScheduleSync(200);
            }
        }
        catch (Exception ex)
        {
            _log.LogException("ReviewRegistry.OnAfterSave", ex);
        }
        return VSConstants.S_OK;
    }

    // The editor has reloaded a file from the disk: for a file that was waiting for that, it is now what the disk holds.
    int IVsRunningDocTableEvents.OnAfterAttributeChange(uint docCookie, uint grfAttribs)
    {
        try
        {
            if ((grfAttribs & (uint)__VSRDTATTRIB.RDTA_DocDataReloaded) == 0) { return VSConstants.S_OK; }
            var path = DocumentPath(docCookie);
            if (IsListed(path) && !_reviews.ContainsKey(path))
            {
                _notYet.Remove(path);
                ScheduleSync(200);
            }
        }
        catch (Exception ex)
        {
            _log.LogException("ReviewRegistry.OnAfterAttributeChange", ex);
        }
        return VSConstants.S_OK;
    }

    int IVsRunningDocTableEvents.OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
}

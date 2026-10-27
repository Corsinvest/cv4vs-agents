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
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>Which listed files are being reviewed in their own editor, process-wide: every chat pane's list is fed
/// in, and each file that is open, and can be shown, gets a <see cref="BaselineDocumentController"/>.
/// <para>It works by reconciliation rather than by events about single files: whenever any list changes, or a document
/// window is about to be shown, <see cref="SyncAsync"/> compares what every pane lists with what is being reviewed, ends the
/// reviews that have lost their row and starts the ones that have gained a file. So a Keep or an Undo in the panel, which
/// only touch the list, end the review without anything having to tell it. A pass is always a turn of the dispatcher of its
/// own and never runs inside another: it edits rows, which asks for a pass.</para>
/// <para>No MEF: documents are found through the running document table, as the comparison window is.</para>
/// <para>A file two panes list is reviewed for one of them (<see cref="ReviewOwnership"/>), and both rows say so
/// (<see cref="FileChange.SharedWithPane"/>): the person is told in the panel, not left to find out in the log.</para>
/// All of it runs on the UI thread.</summary>
internal sealed class BaselineDocumentRegistry : IVsRunningDocTableEvents
{
    public static BaselineDocumentRegistry Instance { get; } = new();

    private readonly OutputWindowLogger _log = OutputWindowLogger.Global;
    private readonly List<SessionChanges> _owners = new();
    private readonly Dictionary<SessionChanges, Action> _subscriptions = new();
    private readonly Dictionary<string, BaselineDocumentController> _controllers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Declined> _declined = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Claim> _claims = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _attaching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IWpfTextView, LoadWait> _waitingForLoad = new();

    private int _syncQueued;
    private bool _syncRunning;
    private bool _syncAgain;
    private bool _servicesTried;
    private IVsEditorAdaptersFactoryService _adapters;
    private ITextDocumentFactoryService _documents;
    private uint _rdtCookie;

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

    private sealed class Parts
    {
        public IWpfTextView View;
        public ITextDocument Document;
        public IVsDifferenceCodeWindow2 Window;
        public IDifferenceTextViewModel Model;
    }

    /// <summary>The editor's adapters, for whoever has to turn a shell object into an editor one. Null before the first sync.</summary>
    public IVsEditorAdaptersFactoryService Adapters => _adapters;

    /// <summary>Start following a pane's list. Called once, when the pane makes it.</summary>
    public void Register(SessionChanges owner)
    {
        if (!ThreadHelper.CheckAccess())
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Register(owner);
            }).FileAndForget("BaselineDocumentRegistry.Register");
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
            }).FileAndForget("BaselineDocumentRegistry.Unregister");
            return;
        }
        if (owner is null || !_owners.Remove(owner)) { return; }
        if (_subscriptions.TryGetValue(owner, out var changed))
        {
            owner.Set.Changed -= changed;
            _subscriptions.Remove(owner);
        }
        foreach (var controller in _controllers.Values.Where(c => ReferenceEquals(c.Owner, owner)).ToList()) { controller.Exit(); }
        ScheduleSync();
    }

    /// <summary>The review whose window <paramref name="view"/> is one of, if any: for the commands, which act on the
    /// view the caret is in.</summary>
    public BaselineDocumentController ControllerFor(IWpfTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return view is null ? null : _controllers.Values.FirstOrDefault(c => c.Matches(view));
    }

    /// <summary>Something a review did may have changed what the lists say about it: look again.</summary>
    public void Poke() => ScheduleSync();

    /// <summary>A review ended. When its view was closed under it (the pane of a split that was removed, say) and the file is
    /// still listed and open, the file gets another review in what is left of the window, a moment later: by then a tab the
    /// person closed is gone from the frames, and nothing is started for it.</summary>
    internal void Detached(BaselineDocumentController controller, bool viewClosed)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_controllers.TryGetValue(controller.Path, out var held) && ReferenceEquals(held, controller)) { _controllers.Remove(controller.Path); }
        if (viewClosed) { ScheduleSync(400); }
    }

    /// <summary>Show a listed file in its own editor with the changes drawn in it, for the pane whose list is
    /// <paramref name="set"/>, opening the file if it is not open. False, with nothing done to the editor, when it cannot be
    /// shown there: the caller then opens the comparison window.</summary>
    public async System.Threading.Tasks.Task<bool> ShowAsync(ChangeSet set, string path)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        SessionChanges owner = null;
        var shown = false;
        try
        {
            if (AgentsOptions.Chat.ChangesViewMode == ChangesViewMode.Files) { return false; }
            EnsureServices();
            if (_adapters is null) { return false; }
            owner = _owners.FirstOrDefault(o => ReferenceEquals(o.Set, set));
            if (owner is null || !set.TryGet(path, out var row)) { return false; }

            // The person asked for this file in this chat: it is this chat's to show, until another chat changes it later.
            _claims[path] = new Claim(owner, DateTime.UtcNow);
            _declined.Remove(path);

            // The editor's view is made a moment after the file is opened, and has to be loaded before it can be switched. The row
            // and whoever holds the file are looked at again each time round: a pass of the registry runs in between.
            var opened = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!set.TryGet(path, out row)) { return false; }
                if (_controllers.TryGetValue(path, out var held))
                {
                    if (ReferenceEquals(held.Owner, owner) && held.RowSerial == row.Serial)
                    {
                        held.Reveal();
                        shown = true;
                        return true;
                    }
                    // Another chat's review of the file, or this chat's of a row that has gone: the file is this chat's to show now.
                    held.Exit();
                }

                if (!opened)
                {
                    if (!await IdeContextService.Instance.OpenFileAsync(path, 0, 0, true)) { return false; }
                    opened = true;
                }

                var frames = IdeContextService.FindDocumentFrames(path);
                if (frames.Count > 0) { await TryAttachAsync(owner, row, frames); }
                if (_controllers.ContainsKey(path)) { continue; }
                if (_declined.ContainsKey(path)) { return false; }
                await Task.Delay(50);
            }
            _log.Info($"[changes] {path} did not become ready to show in place: the comparison window is used");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("BaselineDocumentRegistry.ShowAsync", ex);
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
        }).FileAndForget("BaselineDocumentRegistry.Sync");
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
                    _log.LogException("BaselineDocumentRegistry.SyncAsync", ex);
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
        var mode = AgentsOptions.Chat.ChangesViewMode;
        var form = mode == ChangesViewMode.SideBySide ? DifferenceViewMode.SideBySide : DifferenceViewMode.Inline;

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
            _controllers.TryGetValue(pair.Key, out var held);
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

        // A review whose row is gone (kept, undone, back to how it was), or has been replaced, or whose pane lost the file to
        // another, ends: nothing here tells it, the list is the only thing that changed. One that stays takes the form asked for.
        foreach (var controller in _controllers.Values.ToList())
        {
            if (controller.IsReplacingBaseline) { continue; }
            if (mode == ChangesViewMode.Files
                || !winners.TryGetValue(controller.Path, out var winner)
                || !ReferenceEquals(winner.Owner, controller.Owner)
                || winner.Row.Serial != controller.RowSerial)
            {
                controller.Exit();
            }
            else
            {
                controller.ApplyMode(form);
            }
        }

        if (mode == ChangesViewMode.Files || _adapters is null) { return; }
        // Only a file that is open can be shown: one walk over the frames, not one per row.
        var open = IdeContextService.OpenDocumentFrames();
        foreach (var winner in winners.Values.ToList())
        {
            if (_controllers.ContainsKey(winner.Row.Path)) { continue; }
            if (!open.TryGetValue(IdeContextService.FrameKey(winner.Row.Path), out var frames)) { continue; }
            try { await TryAttachAsync(winner.Owner, winner.Row, frames); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogException("BaselineDocumentRegistry.TryAttach", ex);
            }
        }
    }

    /// <summary>Show a file in place if it is open and can be. A file that is not open is left for the window event that
    /// comes when it is; one that cannot be shown is declined, once, with the reason in the log.</summary>
    private async Task TryAttachAsync(SessionChanges owner, FileChange row, IReadOnlyList<IVsWindowFrame> frames)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = row.Path;
        if (!_attaching.Add(path)) { return; }
        try
        {
            // Reviewed already: the baseline that window holds is this registry's own, not another extension's.
            if (_controllers.ContainsKey(path) || IsDeclined(owner, row)) { return; }

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
                if (why is not null) { Decline(owner, row, why); }
                return;
            }
            if (!parts.View.VisualElement.IsLoaded)
            {
                WaitForLoad(parts.View);
                return;
            }

            ReviewBaseline baseline = null;
            string problem = null;
            await Task.Run(() => { problem = BaselineReview.Prepare(row, out baseline); });
            if (problem is not null)
            {
                Decline(owner, row, problem);
                return;
            }

            // Things may have moved while the baseline was read.
            if (_controllers.ContainsKey(path) || !_owners.Contains(owner) || !IdeContextService.FindDocumentFrames(path).Contains(frame)) { return; }
            if (!owner.Set.TryGet(path, out var now) || now.Serial != row.Serial) { return; }

            var mode = AgentsOptions.Chat.ChangesViewMode == ChangesViewMode.SideBySide ? DifferenceViewMode.SideBySide : DifferenceViewMode.Inline;
            var controller = new BaselineDocumentController(this, owner, now, frame, parts.View, parts.Document, parts.Window, parts.Model, baseline);
            if (!controller.Enter(mode))
            {
                Decline(owner, now, "the editor would not switch to a difference view");
                return;
            }
            // Ended already, by an event of the window during the switch: nothing to keep.
            if (controller.Exited) { return; }
            _controllers[path] = controller;
            _declined.Remove(path);
        }
        finally
        {
            _attaching.Remove(path);
        }
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
            why = "another extension is already showing a difference in it (Copilot, or a Git diff)";
            return null;
        }
        if (!HunkLocator.TryGetModel(view, out var model))
        {
            why = "its view is not a difference view";
            return null;
        }
        return new Parts { View = view, Document = document, Window = window, Model = model };
    }

    private bool IsDeclined(SessionChanges owner, FileChange row)
        => _declined.TryGetValue(row.Path, out var known)
           && ReferenceEquals(known.Owner, owner) && known.Serial == row.Serial
           && known.StampLength == row.StampLength && known.StampWriteUtc == row.StampWriteUtc;

    private void Decline(SessionChanges owner, FileChange row, string why)
    {
        if (IsDeclined(owner, row)) { return; }
        _declined[row.Path] = new Declined { Owner = owner, Serial = row.Serial, StampLength = row.StampLength, StampWriteUtc = row.StampWriteUtc };
        _log.Info($"[changes] {row.Path} is not shown in place: {why}");
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
                _log.Warn("[changes] the editor services are not available: files are not shown in place");
                return;
            }
            if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is IVsRunningDocumentTable rdt)
            {
                rdt.AdviseRunningDocTableEvents(this, out _rdtCookie);
            }
            // The form the person picked applies at once, not at the next change of a list.
            AgentsOptions.Applied += () => ScheduleSync();
        }
        catch (Exception ex)
        {
            _adapters = null;
            _log.LogException("BaselineDocumentRegistry.EnsureServices", ex);
        }
    }

    // A window is about to be shown: if it is a listed file, it is a candidate. The view and its code window may not exist
    // yet on a first show, so the pass is repeated while they come up.
    int IVsRunningDocTableEvents.OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
    {
        try
        {
            var path = pFrame is null ? null : IdeContextService.DocumentMoniker(pFrame);
            if (path is not null && _owners.Any(o => o.Set.TryGet(path, out _)))
            {
                ScheduleSync();
                ScheduleSync(400);
                ScheduleSync(1500);
            }
        }
        catch (Exception ex)
        {
            _log.LogException("BaselineDocumentRegistry.OnBeforeDocumentWindowShow", ex);
        }
        return VSConstants.S_OK;
    }

    int IVsRunningDocTableEvents.OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnAfterSave(uint docCookie) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
    int IVsRunningDocTableEvents.OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
}

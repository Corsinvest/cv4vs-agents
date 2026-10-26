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
using System.Threading;
using System.Windows;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>Which listed files are being reviewed in their own editor, process-wide: every chat pane's list is fed
/// in, and each file that is open, and can be shown, gets a <see cref="BaselineDocumentController"/>.
/// <para>It works by reconciliation rather than by events about single files: whenever any list changes, or a document
/// window is about to be shown, <see cref="SyncAsync"/> compares what every pane lists with what is being reviewed, ends the
/// reviews that have lost their row and starts the ones that have gained a file. So a Keep or an Undo in the panel, which
/// only touch the list, end the review without anything having to tell it.</para>
/// <para>No MEF: documents are found through the running document table, as the comparison window is.</para>
/// <para>A file two panes list is reviewed for one of them, the one whose row changed last, and both rows say so
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
    private readonly HashSet<string> _attaching = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IWpfTextView> _waitingForLoad = new();

    private int _syncQueued;
    private bool _servicesTried;
    private IVsEditorAdaptersFactoryService _adapters;
    private ITextDocumentFactoryService _documents;
    private uint _rdtCookie;

    // A review that ended within this of its window opening was ended by the window, not by the person: see Detached.
    private static readonly TimeSpan EarlyClose = TimeSpan.FromSeconds(3);

    private sealed class Declined
    {
        public long Serial;
        public string Why;
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

    /// <summary>A review ended. A window that closed on its own right after the difference was switched on is not shown that
    /// file again: it is a sign the window does not take it, and trying again would switch it on, and it would close, for ever.</summary>
    internal void Detached(BaselineDocumentController controller, bool viewClosed)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_controllers.TryGetValue(controller.Path, out var held) && ReferenceEquals(held, controller)) { _controllers.Remove(controller.Path); }
        if (viewClosed && controller.EnteredUtc != default && DateTime.UtcNow - controller.EnteredUtc < EarlyClose
            && IdeContextService.FindDocumentFrame(controller.Path) is not null)
        {
            Decline(controller.Path, controller.RowSerial, "the editor closed the view as soon as the difference was switched on");
        }
    }

    /// <summary>Show a listed file in its own editor with the changes drawn in it, for the pane whose list is
    /// <paramref name="set"/>, opening the file if it is not open. False, with nothing done to the editor, when it cannot be
    /// shown there: the caller then opens the comparison window.</summary>
    public async System.Threading.Tasks.Task<bool> ShowAsync(ChangeSet set, string path)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (AgentsOptions.Chat.ChangesViewMode == ChangesViewMode.Files) { return false; }
            EnsureServices();
            if (_adapters is null) { return false; }
            var owner = _owners.FirstOrDefault(o => ReferenceEquals(o.Set, set));
            if (owner is null || !set.TryGet(path, out var row)) { return false; }

            if (_controllers.TryGetValue(path, out var held))
            {
                if (ReferenceEquals(held.Owner, owner) && held.RowSerial == row.Serial)
                {
                    held.Reveal();
                    return true;
                }
                // Asked for from this pane: it takes the review from whoever was showing the file.
                held.Exit();
            }
            _declined.Remove(path);

            if (!await IdeContextService.Instance.OpenFileAsync(path, 0, 0, true)) { return false; }

            // The editor's view is made a moment after the file is opened, and has to be loaded before it can be switched.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var frame = IdeContextService.FindDocumentFrame(path);
                if (frame is not null) { await TryAttachAsync(owner, row, frame); }
                if (_controllers.TryGetValue(path, out var attached) && ReferenceEquals(attached.Owner, owner))
                {
                    attached.Reveal();
                    return true;
                }
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
    }

    /// <summary>Queue a reconciliation on the UI thread, after <paramref name="delayMs"/>. One that is already waiting covers
    /// this one: the lists change in bursts, and a pass looks at everything.</summary>
    private void ScheduleSync(int delayMs = 0)
    {
        if (delayMs == 0 && Interlocked.Exchange(ref _syncQueued, 1) == 1) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            if (delayMs > 0) { await Task.Delay(delayMs); }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (delayMs == 0) { Interlocked.Exchange(ref _syncQueued, 0); }
            await SyncAsync();
        }).FileAndForget("BaselineDocumentRegistry.Sync");
    }

    private async Task SyncAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            EnsureServices();
            var mode = AgentsOptions.Chat.ChangesViewMode;

            var listed = new Dictionary<string, List<Listing>>(StringComparer.OrdinalIgnoreCase);
            foreach (var owner in _owners.ToList())
            {
                foreach (var row in owner.Set.Snapshot())
                {
                    if (!listed.TryGetValue(row.Path, out var rows)) { listed[row.Path] = rows = new List<Listing>(); }
                    rows.Add(new Listing(owner, row));
                }
            }

            var winners = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in listed) { winners[pair.Key] = ChooseWinner(pair.Key, pair.Value); }
            MarkShared(listed, winners);

            // A review whose row is gone (kept, undone, back to how it was), or has been replaced, or whose pane lost the
            // file to another, ends: nothing here tells it, the list is the only thing that changed.
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
            }

            _declined.Keys.Where(p => !winners.TryGetValue(p, out var w) || w.Row.Serial != _declined[p].Serial).ToList()
                     .ForEach(p => _declined.Remove(p));

            if (mode == ChangesViewMode.Files || _adapters is null) { return; }
            // Only a file that is open can be shown: one walk over the frames, not one per row.
            var open = IdeContextService.OpenDocumentFrames();
            foreach (var winner in winners.Values)
            {
                if (_controllers.ContainsKey(winner.Row.Path)) { continue; }
                if (!open.TryGetValue(IdeContextService.FrameKey(winner.Row.Path), out var frame)) { continue; }
                await TryAttachAsync(winner.Owner, winner.Row, frame);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("BaselineDocumentRegistry.SyncAsync", ex);
        }
    }

    /// <summary>Whose review a file gets when more than one pane lists it: the one whose row changed last, and the one that has
    /// it already when they tie, so that two panes do not take it from each other.</summary>
    private Listing ChooseWinner(string path, List<Listing> listings)
    {
        var current = _controllers.TryGetValue(path, out var held) ? held.Owner : null;
        return listings
            .OrderByDescending(l => l.Row.LastChangedUtc)
            .ThenByDescending(l => ReferenceEquals(l.Owner, current))
            .First();
    }

    /// <summary>Tell every row of a file two panes list which other pane has it, and clear it from a row that is alone again.
    /// Only a row that has to change is touched: a touch is a change, and a change comes back here.</summary>
    private static void MarkShared(Dictionary<string, List<Listing>> listed, Dictionary<string, Listing> winners)
    {
        foreach (var pair in listed)
        {
            var winner = winners[pair.Key];
            foreach (var listing in pair.Value)
            {
                int? shared = null;
                if (pair.Value.Count > 1)
                {
                    var other = ReferenceEquals(listing.Owner, winner.Owner)
                        ? pair.Value.First(l => !ReferenceEquals(l.Owner, listing.Owner))
                        : winner;
                    shared = other.Owner.PaneId;
                }
                if (listing.Row.SharedWithPane == shared) { continue; }
                listing.Owner.Set.Update(pair.Key, r => r.SharedWithPane = shared);
            }
        }
    }

    /// <summary>Show a file in place if it is open and can be. A file that is not open is left for the window event that
    /// comes when it is; one that cannot be shown is declined, once, with the reason in the log.</summary>
    private async Task TryAttachAsync(SessionChanges owner, FileChange row, IVsWindowFrame frame)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var path = row.Path;
        if (!_attaching.Add(path)) { return; }
        try
        {
            if (_declined.TryGetValue(path, out var declined) && declined.Serial == row.Serial) { return; }

            var parts = Inspect(frame, out var why, out var pending);
            if (parts is null)
            {
                if (!pending) { Decline(path, row.Serial, why); }
                return;
            }
            if (!parts.View.VisualElement.IsLoaded)
            {
                WaitForLoad(parts.View);
                return;
            }

            ReviewBaseline baseline = null;
            string reason = null;
            await Task.Run(() => { reason = BaselineReview.Prepare(row, out baseline); });
            if (reason is not null)
            {
                Decline(path, row.Serial, reason);
                return;
            }

            // Things may have moved while the baseline was read.
            if (_controllers.ContainsKey(path) || IdeContextService.FindDocumentFrame(path) != frame) { return; }
            if (!owner.Set.TryGet(path, out var now) || now.Serial != row.Serial) { return; }

            var mode = AgentsOptions.Chat.ChangesViewMode == ChangesViewMode.SideBySide ? DifferenceViewMode.SideBySide : DifferenceViewMode.Inline;
            var controller = new BaselineDocumentController(this, owner, now, frame, parts.View, parts.Document, parts.Window, parts.Model, baseline);
            if (!controller.Enter(mode))
            {
                Decline(path, row.Serial, "the editor would not switch to a difference view");
                return;
            }
            _controllers[path] = controller;
            _declined.Remove(path);
        }
        finally
        {
            _attaching.Remove(path);
        }
    }

    private sealed class Parts
    {
        public IWpfTextView View;
        public ITextDocument Document;
        public IVsDifferenceCodeWindow2 Window;
        public IDifferenceTextViewModel Model;
    }

    /// <summary>What the frame's editor is made of, or null with <paramref name="why"/>. <paramref name="pending"/> says the
    /// answer is "not yet": the view of a document that has only just been opened does not exist for a moment.</summary>
    private Parts Inspect(IVsWindowFrame frame, out string why, out bool pending)
    {
        why = null;
        pending = false;
        var vsView = VsShellUtilities.GetTextView(frame);
        var view = vsView is null ? null : _adapters.GetWpfTextView(vsView);
        if (view is null)
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

    private void Decline(string path, long serial, string why)
    {
        if (_declined.TryGetValue(path, out var known) && known.Serial == serial) { return; }
        _declined[path] = new Declined { Serial = serial, Why = why };
        _log.Info($"[changes] {path} is not shown in place: {why}");
    }

    // The view of a document that has only just opened is not loaded yet, and the editor will not take a baseline until it is.
    private void WaitForLoad(IWpfTextView view)
    {
        if (!_waitingForLoad.Add(view)) { return; }
        RoutedEventHandler loaded = null;
        loaded = (_, _) =>
        {
            view.VisualElement.Loaded -= loaded;
            _waitingForLoad.Remove(view);
            ScheduleSync();
        };
        view.VisualElement.Loaded += loaded;
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>
/// Keeps the status bar's picture of plan usage current, for the profile of the pane last worked in.
/// <para>Numbers come from the cheapest source that has them. A chat pane on that profile answers
/// get_usage from its own running claude.exe after each of its turns, and its rate_limit_events arrive
/// for free. Only with no such pane open is a short-lived claude.exe started — and then only when the
/// numbers are older than the configured interval while VS is in front, when the popup opens on old
/// numbers, or on Refresh.</para>
/// <para>UI thread throughout; only the probe itself runs elsewhere.</para>
/// </summary>
internal sealed class UsageStatusService
{
    private static readonly Lazy<UsageStatusService> _instance = new(() => new UsageStatusService());

    public static UsageStatusService Instance => _instance.Value;

    // A turn's usage is counted a moment after its result arrives; asking at once can miss it.
    private static readonly TimeSpan TurnSettleDelay = TimeSpan.FromSeconds(2);

    // One pane refresh per profile in this span however many turns end: a busy session would otherwise
    // ask after every exchange.
    private static readonly TimeSpan TurnRefreshSpacing = TimeSpan.FromMinutes(1);

    // Numbers older than this are replaced when the popup opens, background refresh or not.
    private static readonly TimeSpan PopupStaleAfter = TimeSpan.FromMinutes(2);

    // Unrequested refreshes hold off this long after an answer without a plan, or after a failure:
    // asking again sooner would only repeat it.
    private static readonly TimeSpan NoPlanRetryAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan ErrorRetryAfter = TimeSpan.FromMinutes(5);

    // The first background look waits for VS to finish starting. After that the clock ticks each minute,
    // which also keeps "in 2h 31m" counting down and lets a window read 0 once it has reset.
    private static readonly TimeSpan FirstTick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, UsageSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTurnRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _turnTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private Profile _current;
    private bool _started;
    private Timer _tick;
    private IVsMonitorSelection _monitorSelection;
    private uint _selectionCookie;
    private Window _mainWindow;
    private CancellationTokenSource _cts = new();

    private UsageStatusService() { }

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> may read differently.</summary>
    public event Action Changed;

    /// <summary>The shown profile's usage. Never null.</summary>
    public UsageSnapshot Current => _current == null ? UsageSnapshot.Initial("Claude") : Get(_current.Name);

    private static TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(0, AgentsOptions.General.UsageRefreshMinutes));

    public void Start()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_started) { return; }
        _started = true;
        _cts = new CancellationTokenSource();
        _current = ProfileStore.Load(forEdit: false).FirstOrDefault();

        _monitorSelection = Package.GetGlobalService(typeof(SVsShellMonitorSelection)) as IVsMonitorSelection;
        if (_monitorSelection != null)
        {
            _monitorSelection.AdviseSelectionEvents(new SelectionEventSink(this), out _selectionCookie);
            // A pane may have focus already: the item comes up at shell idle, after any pane restore.
            if (_monitorSelection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out var active) == VSConstants.S_OK)
            {
                Track(active as IVsWindowFrame);
            }
        }

        _mainWindow = Application.Current?.MainWindow;
        if (_mainWindow != null) { _mainWindow.Activated += OnMainWindowActivated; }

        _tick = new Timer(_ => OnUiThread(OnTick), null, FirstTick, TickInterval);
        OutputWindowLogger.Global.Info($"[usage-status] started, showing profile '{_current?.Name}'");
        Changed?.Invoke();
    }

    public void Stop()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started) { return; }
        _started = false;
        if (_selectionCookie != 0)
        {
            _monitorSelection?.UnadviseSelectionEvents(_selectionCookie);
            _selectionCookie = 0;
        }
        if (_mainWindow != null)
        {
            _mainWindow.Activated -= OnMainWindowActivated;
            _mainWindow = null;
        }
        _tick?.Dispose();
        _tick = null;
        foreach (var timer in _turnTimers.Values) { timer.Dispose(); }
        _turnTimers.Clear();
        // A probe still starting its claude.exe gives up instead of publishing into a stopped service.
        _cts.Cancel();
        OutputWindowLogger.Global.Info("[usage-status] stopped");
    }

    /// <summary>A chat pane's turn ended, so its profile's usage moved. Re-read from that pane's own
    /// process, at most once a minute per profile.</summary>
    public void OnTurnEnded(PaneEntry entry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started || entry?.FetchUsageAction == null) { return; }
        var profile = entry.Profile;
        var name = profile.Name;
        // The refresh already waiting covers this turn too.
        if (_turnTimers.ContainsKey(name)) { return; }

        var now = DateTimeOffset.Now;
        var due = now + TurnSettleDelay;
        if (_lastTurnRefresh.TryGetValue(name, out var last) && last + TurnRefreshSpacing > due) { due = last + TurnRefreshSpacing; }

        Timer timer = null;
        timer = new Timer(_ => OnUiThread(() =>
        {
            timer.Dispose();
            // Gone from the table: stopped in the meantime.
            if (!_turnTimers.Remove(name)) { return; }
            _lastTurnRefresh[name] = DateTimeOffset.Now;
            Fire(() => RefreshAsync(profile, allowProbe: false));
        }), null, due - now, Timeout.InfiniteTimeSpan);
        _turnTimers[name] = timer;
    }

    /// <summary>A rate_limit_event from a chat pane: the window it names changes at once, ahead of the
    /// full refresh after the turn.</summary>
    public void OnRateLimit(PaneEntry entry, RateLimitInfo info)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started || entry == null || info == null) { return; }
        var snapshot = Get(entry.Profile.Name);
        var windows = snapshot.Usage?.Windows ?? [];
        var updated = UsageStatusFormat.ApplyRateLimit(windows, info);
        if (!ReferenceEquals(updated, windows)) { Publish(snapshot.WithWindows(updated)); }
    }

    /// <summary>A pane fetched usage for its own reasons — the chat's Account &amp; Usage dialog — which is
    /// a fresh answer for the status bar as well.</summary>
    public void OnUsageFetched(PaneEntry entry, UsageDto usage)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started || entry == null || usage == null) { return; }
        Publish(Get(entry.Profile.Name).WithUsage(usage, DateTimeOffset.Now, entry.Title));
    }

    /// <summary>The popup opened: numbers a couple of minutes old are replaced, background refresh or
    /// not — the user is looking.</summary>
    public void OnPopupOpened()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_started && _current != null) { MaybeRefresh(_current, PopupStaleAfter, allowProbe: true); }
    }

    /// <summary>The popup's Refresh: now, past every hold-off.</summary>
    public void RefreshNow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_started && _current != null) { Fire(() => RefreshAsync(_current, allowProbe: true)); }
    }

    /// <summary>Options were applied: a profile may have been renamed, removed or re-pointed.</summary>
    public void OnOptionsApplied()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started) { return; }
        var profiles = ProfileStore.Load(forEdit: false);
        foreach (var gone in _snapshots.Keys
            .Where(n => !profiles.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            _snapshots.Remove(gone);
        }
        var name = _current?.Name;
        SetCurrent(profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault());
        Changed?.Invoke();
    }

    // Only our own panes move the status bar to another profile; focus going to the editor or anything
    // else leaves it on the last pane's.
    private void Track(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (frame == null || frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view) != VSConstants.S_OK) { return; }
        if (view is PaneWindowBase pane && pane.Entry?.Profile is Profile profile) { SetCurrent(profile); }
    }

    private void SetCurrent(Profile profile)
    {
        if (profile == null) { return; }
        var switched = !string.Equals(profile.Name, _current?.Name, StringComparison.OrdinalIgnoreCase);
        // Same name or not, keep the newest instance: its env is the one a probe should start with.
        _current = profile;
        if (!switched) { return; }
        OutputWindowLogger.Global.Debug(() => $"[usage-status] showing profile '{profile.Name}'");
        Changed?.Invoke();
        MaybeRefresh(profile, Interval, allowProbe: Interval > TimeSpan.Zero);
    }

    private void OnTick()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_started) { return; }
        Changed?.Invoke();
        if (_current == null || Interval <= TimeSpan.Zero || !Win32Focus.IsVsForeground()) { return; }
        MaybeRefresh(_current, Interval, allowProbe: true);
    }

    private void OnMainWindowActivated(object sender, EventArgs e)
    {
        if (!_started || _current == null || Interval <= TimeSpan.Zero) { return; }
        MaybeRefresh(_current, Interval, allowProbe: true);
    }

    private void MaybeRefresh(Profile profile, TimeSpan staleAfter, bool allowProbe)
    {
        var snapshot = Get(profile.Name);
        var now = DateTimeOffset.Now;
        if (_inFlight.Contains(profile.Name)) { return; }
        if (snapshot.FetchedAt is DateTimeOffset fetched && now - fetched < staleAfter) { return; }
        if (_lastAttempt.TryGetValue(profile.Name, out var attempted))
        {
            var holdOff = snapshot.State == UsageAvailability.NoPlan ? NoPlanRetryAfter
                : snapshot.LastErrorAt != null ? ErrorRetryAfter
                : TimeSpan.Zero;
            if (now - attempted < holdOff) { return; }
        }
        Fire(() => RefreshAsync(profile, allowProbe));
    }

    private async Task RefreshAsync(Profile profile, bool allowProbe)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var name = profile.Name;
        if (UsageStatusFormat.HasNoPlan(profile.Env))
        {
            if (Get(name).State != UsageAvailability.NoPlan) { Publish(UsageSnapshot.Initial(name, UsageAvailability.NoPlan)); }
            return;
        }
        var pane = LivePaneFor(profile);
        if (pane == null && !allowProbe) { return; }
        if (pane == null && ClaudeInstall.ResolveExecutable() == null)
        {
            Publish(UsageSnapshot.Initial(name, UsageAvailability.CliMissing));
            return;
        }
        if (!_inFlight.Add(name)) { return; }

        _lastAttempt[name] = DateTimeOffset.Now;
        Publish(Get(name).WithFetching(true));
        var token = _cts.Token;
        try
        {
            UsageDto usage;
            string source;
            if (pane != null)
            {
                OutputWindowLogger.Global.Debug(() => $"[usage-status] refreshing '{name}' through {pane.Title}");
                usage = await pane.FetchUsageAction();
                source = pane.Title;
            }
            else
            {
                OutputWindowLogger.Global.Debug(() => $"[usage-status] refreshing '{name}' with a background claude.exe");
                var (raw, account) = await Task.Run(() => UsageProbe.FetchRawAsync(profile, null, token), token);
                usage = raw == null ? null : UsageMapper.Build(raw, account);
                source = "background";
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            if (usage == null)
            {
                OutputWindowLogger.Global.Warn($"[usage-status] no usage for profile '{name}': get_usage returned an error");
                Publish(Get(name).WithError("the CLI returned no usage", DateTimeOffset.Now));
            }
            else
            {
                Publish(Get(name).WithUsage(usage, DateTimeOffset.Now, source));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped while fetching: nothing more to publish.
        }
        catch (Exception ex)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            OutputWindowLogger.Global.Warn($"[usage-status] refresh failed for profile '{name}': {ex.Message}");
            Publish(Get(name).WithError(ex.Message, DateTimeOffset.Now));
        }
        finally
        {
            _inFlight.Remove(name);
        }
    }

    // The most recently opened chat pane on the profile whose process can answer.
    private static PaneEntry LivePaneFor(Profile profile)
        => PaneRegistry.Instance.OfKind(PaneKind.Chat).LastOrDefault(e =>
            e.FetchUsageAction != null && string.Equals(e.Profile.Name, profile.Name, StringComparison.OrdinalIgnoreCase));

    private void Publish(UsageSnapshot snapshot)
    {
        if (!_started) { return; }
        _snapshots[snapshot.ProfileName] = snapshot;
        if (string.Equals(snapshot.ProfileName, _current?.Name, StringComparison.OrdinalIgnoreCase)) { Changed?.Invoke(); }
    }

    private UsageSnapshot Get(string name)
        => _snapshots.TryGetValue(name ?? "", out var snapshot) ? snapshot : UsageSnapshot.Initial(name);

    private static void Fire(Func<Task> work)
        => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await work(); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("[usage-status] refresh", ex); }
        }).FileAndForget(nameof(UsageStatusService));

    // Timer callbacks arrive on the thread pool; everything here lives on the UI thread.
    private static void OnUiThread(Action action)
        => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try { action(); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("[usage-status] timer", ex); }
        }).FileAndForget(nameof(UsageStatusService));

    /// <summary>IVsMonitorSelection sink: follows the active window frame to the pane under it.</summary>
    private sealed class SelectionEventSink(UsageStatusService owner) : IVsSelectionEvents
    {
        int IVsSelectionEvents.OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (owner._started && elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
            {
                owner.Track(varValueNew as IVsWindowFrame);
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

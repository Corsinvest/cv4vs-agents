/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Power;

/// <summary>Keeps Windows from suspending the machine while a turn is running.
/// <para>A turn is an interval between two messages from a process we do not control, so the
/// closing message may never arrive. Hence a set keyed by pane rather than a refcount: every exit
/// — result, process death, pane closed, silence — removes an id, and removing one twice is a
/// no-op. One native request covers the whole set.</para></summary>
internal sealed class KeepAwakeService
{
    internal const long SilenceLimitMs = 5 * 60 * 1000;

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    // Divide once, not on every clock read: Frequency is not a compile-time constant, so the JIT
    // cannot fold it.
    private static readonly long TicksPerMs = Stopwatch.Frequency / 1000;

    /// <summary>Monotonic milliseconds. DateTime.Now moves under us on DST, an NTP correction or a
    /// resume — on a feature about suspension, that is the normal case — and Environment.TickCount
    /// wraps negative at ~25 days of uptime. Stopwatch is net48's TickCount64.</summary>
    private static long Now() => Stopwatch.GetTimestamp() / TicksPerMs;

    /// <summary>What a pane gets while a legitimately silent phase is running. Long enough that a
    /// healthy compaction finishes well inside it, short enough that a wedged one is still
    /// released.</summary>
    internal const long RelaxedSilenceLimitMs = 3 * SilenceLimitMs;

    // What `powercfg /requests` shows the user, so it has to name us the way everything else does.
    private const string Reason = AppConstants.AppName + ": a chat turn is running";

    private static readonly Lazy<KeepAwakeService> _instance = new(() => new KeepAwakeService());

    public static KeepAwakeService Instance => _instance.Value;

    private readonly Func<string, IDisposable> _requestFactory;
    private readonly Func<long> _clock;
    private readonly HashSet<int> _busy = [];                // panes that must not be interrupted
    private readonly Dictionary<int, long> _lastSignal = []; // last proof of life, per pane
    private readonly HashSet<int> _watchdogRelaxed = [];     // panes in a legitimately silent phase
    private readonly object _gate = new();

    private IDisposable _request;

    // Latches the "could not create" log so a permanently failing request does not fill the
    // Output window; cleared by the next success.
    private bool _createFailed;

    // Null under the test constructor, which drives CheckSilence directly.
    private readonly Timer _watchdog;
    private bool _watchdogRunning;

    // Set once at teardown. A pane event can still land after it — VS disposes the package while
    // tool windows may outlive it — and acquiring then would leave a request nobody releases.
    private bool _shutdown;

    private KeepAwakeService() : this(PowerRequest.Create, Now)
        => _watchdog = new Timer(_ => CheckSilence(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    internal KeepAwakeService(Func<string, IDisposable> requestFactory, Func<long> clock)
    {
        _requestFactory = requestFactory;
        _clock = clock;
    }

    public bool IsBlocking { get { lock (_gate) { return _request != null; } } }

    /// <summary>Whether this pane is doing work that must not be interrupted.</summary>
    public void SetBusy(int seqNo, bool busy)
    {
        lock (_gate)
        {
            if (busy)
            {
                _busy.Add(seqNo);
                _lastSignal[seqNo] = _clock();   // going busy is itself proof of life
            }
            else { Drop(seqNo); }

            Sync();
        }
    }

    /// <summary>The pane is gone or its process died. Same as going idle, but says why at the call
    /// site — these are the paths where no "turn ended" message will ever arrive.</summary>
    public void Forget(int seqNo) => SetBusy(seqNo, false);

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _busy.Clear();
            _lastSignal.Clear();
            _watchdogRelaxed.Clear();
            Sync();
        }
    }

    /// <summary>The CLI said something, so it is alive. Resets this pane's silence clock.</summary>
    public void Signal(int seqNo)
    {
        lock (_gate)
        {
            if (_busy.Contains(seqNo)) { _lastSignal[seqNo] = _clock(); }
        }
    }

    /// <summary>Grants the longer limit for a phase that produces no output (compaction). A limit,
    /// not an exemption: a CLI that wedges mid-phase never sends the closing message.</summary>
    public void RelaxWatchdog(int seqNo, bool relaxed)
    {
        lock (_gate)
        {
            // Busy-gated like Signal: a pane we are not holding has nothing to relax, and an id
            // added while idle would sit in the set until its next turn happened to clear it.
            if (relaxed && _busy.Contains(seqNo)) { _watchdogRelaxed.Add(seqNo); }
            else { _watchdogRelaxed.Remove(seqNo); }
        }
    }

    /// <summary>Releases panes that have gone quiet: a wedged CLI sends neither `result` nor an
    /// exit, so without this the block would outlive the turn by the lifetime of the IDE.</summary>
    public void CheckSilence()
    {
        var expired = new List<(int Seq, long Silent)>();
        lock (_gate)
        {
            var now = _clock();
            foreach (var seq in _busy)
            {
                if (_lastSignal.TryGetValue(seq, out var last) && now - last > LimitFor(seq))
                {
                    expired.Add((seq, now - last));
                }
            }

            foreach (var (seq, _) in expired) { Drop(seq); }

            Sync();
        }

        // Warn, not Debug: this is the only trace that will ever explain a machine that would
        // not sleep, and it means an event we expected never arrived.
        foreach (var (seq, silent) in expired)
        {
            OutputWindowLogger.Global.Warn($"[power] pane #{seq} silent for {silent / 60000} min — releasing the sleep block");
        }
    }

    /// <summary>Forgets one pane, from every collection. Caller holds the gate.</summary>
    private void Drop(int seqNo)
    {
        _busy.Remove(seqNo);
        _lastSignal.Remove(seqNo);
        _watchdogRelaxed.Remove(seqNo);
    }

    /// <summary>How long this pane may stay silent before the watchdog releases it.</summary>
    private long LimitFor(int seqNo) => _watchdogRelaxed.Contains(seqNo) ? RelaxedSilenceLimitMs : SilenceLimitMs;

    /// <summary>Brings the single native request in line with the set. Caller holds the gate.</summary>
    private void Sync()
    {
        if (_busy.Count > 0 && _request == null && !_shutdown)
        {
            try
            {
                _request = _requestFactory(Reason);
                _createFailed = false;
                OutputWindowLogger.Global.Debug(() => $"[power] holding the machine awake ({_busy.Count} pane(s) busy)");
            }
            catch (Exception ex)
            {
                // LogException ignores the log level, so a permanently failing request would write
                // a stack trace on every Sync and every tick. Retry silently, report the streak once.
                if (!_createFailed)
                {
                    _createFailed = true;
                    OutputWindowLogger.Global.LogException("[power] could not create the power request", ex);
                }

                _request = null;
            }
        }
        else if (_busy.Count == 0 && _request != null)
        {
            try { _request.Dispose(); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("[power] could not clear the power request", ex); }
            finally
            {
                _request = null;
                OutputWindowLogger.Global.Debug(() => "[power] released, the machine may sleep");
            }
        }

        SetWatchdogRunning(_busy.Count > 0);
    }

    /// <summary>Runs the watchdog only while something is held — a tick on an idle machine is what
    /// this feature exists to avoid. Caller holds the gate.</summary>
    private void SetWatchdogRunning(bool running)
    {
        if (_watchdog == null || _shutdown || running == _watchdogRunning) { return; }
        _watchdogRunning = running;
        _watchdog.Change(running ? WatchdogInterval : Timeout.InfiniteTimeSpan,
                         running ? WatchdogInterval : Timeout.InfiniteTimeSpan);
    }

    /// <summary>Shuts the service down only if something ever started it. A session where no turn
    /// ran never built the singleton, and constructing it at teardown just to stop it would create
    /// a timer nobody asked for.</summary>
    public static void ShutdownIfStarted()
    {
        if (_instance.IsValueCreated) { _instance.Value.Shutdown(); }
    }

    /// <summary>Visual Studio is closing. The OS would drop the handle with the process anyway,
    /// but leaving it to that hides a leak while developing.</summary>
    internal void Shutdown()
    {
        ReleaseAll();       // clears the set, which stops the watchdog through Sync
        lock (_gate) { _shutdown = true; }
        _watchdog?.Dispose();
    }
}

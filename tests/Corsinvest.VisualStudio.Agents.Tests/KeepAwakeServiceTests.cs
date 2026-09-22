/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using Corsinvest.VisualStudio.Agents.Core.Power;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What keeps the machine awake, and — the part that matters — what lets it sleep again.
/// <para>A leaked block is invisible: nothing in the UI shows it, and the machine simply stops
/// sleeping until Visual Studio exits. Every test here is a way out of "busy".</para></summary>
public class KeepAwakeServiceTests
{
    /// <summary>Counts what the service asked the OS for, so the bookkeeping can be checked
    /// without a real power request.</summary>
    private sealed class FakeRequests
    {
        public int Attempted;
        public int Created;
        public int Disposed;
        public bool Fails;

        public IDisposable Create(string reason)
        {
            Attempted++;
            if (Fails) { throw new InvalidOperationException("power request refused"); }
            Created++;
            return new Handle(this);
        }

        private sealed class Handle(FakeRequests owner) : IDisposable
        {
            public void Dispose() => owner.Disposed++;
        }
    }

    private static (KeepAwakeService guard, FakeRequests requests, List<long> now) Create()
    {
        var requests = new FakeRequests();
        var now = new List<long> { 0 };
        return (new KeepAwakeService(requests.Create, () => now[0]), requests, now);
    }

    [Fact]
    public void One_native_request_covers_many_busy_panes()
    {
        var (guard, requests, _) = Create();

        guard.SetBusy(1, true);
        guard.SetBusy(2, true);
        guard.SetBusy(3, true);

        // The system is awake or it is not: three panes must not mean three handles.
        Assert.Equal(1, requests.Created);
        Assert.True(guard.IsBlocking);
    }

    [Fact]
    public void The_request_is_released_only_when_the_last_pane_goes_idle()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);
        guard.SetBusy(2, true);

        guard.SetBusy(1, false);
        Assert.Equal(0, requests.Disposed);
        Assert.True(guard.IsBlocking);

        guard.SetBusy(2, false);
        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void Going_busy_twice_is_not_two_leases()
    {
        var (guard, requests, _) = Create();

        // The CLI can report a status more than once inside one turn. A counter would need two
        // releases to match; the set does not.
        guard.SetBusy(1, true);
        guard.SetBusy(1, true);
        guard.SetBusy(1, false);

        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void Releasing_a_pane_that_was_never_busy_leaves_the_others_alone()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);

        // An id nobody registered must not release the request the other pane is holding — the
        // set makes this a no-op where a counter would have decremented.
        guard.SetBusy(99, false);
        guard.Forget(99);

        Assert.Equal(1, requests.Created);
        Assert.Equal(0, requests.Disposed);
        Assert.True(guard.IsBlocking);
    }

    [Fact]
    public void Forget_releases_a_pane_that_died_mid_turn()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);

        // claude.exe was killed: no `result` will ever arrive. This is the leak the set exists for.
        guard.Forget(1);

        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void Result_and_process_exit_both_arriving_is_harmless()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);

        guard.SetBusy(1, false);   // result
        guard.Forget(1);           // ProcessExited right after

        // A counter would go negative here and the next turn would never block.
        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void ReleaseAll_drops_everything()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);
        guard.SetBusy(2, true);

        guard.ReleaseAll();

        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void Nothing_is_acquired_after_shutdown()
    {
        var (guard, requests, _) = Create();
        guard.SetBusy(1, true);

        guard.Shutdown();
        Assert.Equal(1, requests.Disposed);

        // VS disposes the package while a tool window may still be alive, so a pane event can land
        // after teardown. Acquiring then would leave a request with nobody to release it.
        guard.SetBusy(1, true);

        Assert.Equal(1, requests.Created);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void A_failing_power_request_never_breaks_the_turn()
    {
        var requests = new FakeRequests { Fails = true };
        var guard = new KeepAwakeService(requests.Create, () => 0);

        // A comfort feature must not take the turn down with it: the throw is swallowed and the
        // service simply reports that it is holding nothing.
        guard.SetBusy(1, true);
        guard.SetBusy(1, false);

        // It tried and recovered — without the attempt count this would also pass if the service
        // had never called the factory at all.
        Assert.Equal(1, requests.Attempted);
        Assert.Equal(0, requests.Created);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void A_silent_pane_is_released_by_the_watchdog()
    {
        var (guard, requests, now) = Create();
        guard.SetBusy(1, true);

        now[0] = KeepAwakeService.SilenceLimitMs + 1;
        guard.CheckSilence();

        // claude.exe is alive but wedged: neither `result` nor ProcessExited will come.
        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void Traffic_keeps_the_watchdog_off()
    {
        var (guard, requests, now) = Create();
        guard.SetBusy(1, true);

        now[0] = KeepAwakeService.SilenceLimitMs - 1;
        guard.Signal(1);
        now[0] = KeepAwakeService.SilenceLimitMs + 1;
        guard.CheckSilence();

        Assert.Equal(0, requests.Disposed);
        Assert.True(guard.IsBlocking);
    }

    [Fact]
    public void A_relaxed_pane_gets_the_longer_limit()
    {
        var (guard, requests, now) = Create();
        guard.SetBusy(1, true);

        // Compaction is genuinely silent, so it buys a longer limit — not an exemption.
        guard.RelaxWatchdog(1, true);
        now[0] = KeepAwakeService.SilenceLimitMs + 1;
        guard.CheckSilence();

        Assert.Equal(0, requests.Disposed);

        guard.RelaxWatchdog(1, false);
        guard.CheckSilence();
        Assert.Equal(1, requests.Disposed);
    }

    [Fact]
    public void Relaxing_a_pane_we_are_not_holding_is_a_no_op()
    {
        var (guard, requests, now) = Create();

        // A compacting status can arrive for a pane the service is not holding — the option is off,
        // or the turn already ended. Recording it anyway would leave an id in the relaxed set that
        // only the pane's next turn happens to clear.
        guard.RelaxWatchdog(1, true);
        guard.SetBusy(1, true);

        now[0] = KeepAwakeService.SilenceLimitMs + 1;
        guard.CheckSilence();

        // Still on the base limit: the stale relax did not survive into this turn.
        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void A_relaxed_pane_is_still_released_once_the_longer_limit_passes()
    {
        var (guard, requests, now) = Create();
        guard.SetBusy(1, true);

        // The CLI wedged mid-compaction: the status that would have ended the relaxed phase never
        // arrives. An exemption would hold the machine awake for the lifetime of the IDE, which is
        // the failure the watchdog exists to catch.
        guard.RelaxWatchdog(1, true);
        now[0] = KeepAwakeService.RelaxedSilenceLimitMs + 1;
        guard.CheckSilence();

        Assert.Equal(1, requests.Disposed);
        Assert.False(guard.IsBlocking);
    }

    [Fact]
    public void The_watchdog_only_releases_the_pane_that_went_quiet()
    {
        var (guard, requests, now) = Create();
        guard.SetBusy(1, true);
        guard.SetBusy(2, true);

        now[0] = KeepAwakeService.SilenceLimitMs - 1;
        guard.Signal(2);
        now[0] = KeepAwakeService.SilenceLimitMs + 1;
        guard.CheckSilence();

        Assert.Equal(0, requests.Disposed);
        Assert.True(guard.IsBlocking);

        guard.SetBusy(2, false);
        Assert.Equal(1, requests.Disposed);
    }
}

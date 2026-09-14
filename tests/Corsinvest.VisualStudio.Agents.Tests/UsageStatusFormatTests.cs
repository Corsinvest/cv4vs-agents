/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Usage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What the status bar item says about plan usage.
/// <para>Read at a glance and never inspected, so its mistakes look plausible: a reset shown in the wrong
/// zone, a rate-limit event's fraction taken for a percentage, a window still reading 95% after it has
/// reset.</para></summary>
public class UsageStatusFormatTests
{
    // A Sunday afternoon in UTC with the invariant culture: the wording under test, not the machine's.
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 17, 38, 0, TimeSpan.Zero);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static RateWindowDto Window(string kind, int percent, string resetsAt = null, string severity = null, string name = null)
        => new()
        {
            Kind = kind,
            Name = name ?? UsageMapper.WindowName(kind),
            Utilization = percent,
            ResetsAt = resetsAt,
            Severity = severity,
        };

    private static UsageSnapshot Snapshot(params RateWindowDto[] windows)
        => UsageSnapshot.Initial("Claude").WithUsage(
            new UsageDto { Plan = "Claude max", RateLimitsAvailable = true, Windows = windows }, Now, "Chat 1");

    [Fact]
    public void Status_text_shows_the_session_then_the_week()
    {
        // Listed week first on purpose: the order comes from the kinds, not from the payload.
        var snapshot = Snapshot(Window(UsageMapper.WeeklyKind, 10), Window(UsageMapper.SessionKind, 44));

        Assert.Equal("Claude: 5h 44% · 7d 10%", UsageStatusFormat.StatusText(snapshot, Now));
    }

    [Fact]
    public void Status_text_leaves_out_a_week_the_CLI_did_not_report()
    {
        // A per-model weekly is not "the week": it caps one model, not every request.
        var snapshot = Snapshot(Window(UsageMapper.SessionKind, 44), Window(UsageMapper.WeeklyScopedKind, 13));

        Assert.Equal("Claude: 5h 44%", UsageStatusFormat.StatusText(snapshot, Now));
    }

    [Theory]
    [InlineData("Unknown", "Claude")]
    [InlineData("NoPlan", "Claude")]
    [InlineData("CliMissing", "Claude")]
    // Tried and failed with nothing older to show: the dash tells that apart from "nothing to ask".
    [InlineData("Unavailable", "Claude: —")]
    public void Status_text_without_numbers_is_the_profile_name(string state, string expected)
    {
        var snapshot = UsageSnapshot.Initial("Claude", (UsageAvailability)Enum.Parse(typeof(UsageAvailability), state));

        Assert.Equal(expected, UsageStatusFormat.StatusText(snapshot, Now));
    }

    [Fact]
    public void A_window_past_its_reset_reads_zero_until_the_next_refresh()
    {
        var snapshot = Snapshot(Window(UsageMapper.SessionKind, 95, Now.AddMinutes(-1).ToString("o", Invariant), "critical"));

        var segment = Assert.Single(UsageStatusFormat.Segments(snapshot, Now));
        Assert.Equal(0, segment.Percent);
        Assert.Equal(UsageLevel.Normal, segment.Level);
    }

    [Theory]
    [InlineData(74, null, "Normal")]
    [InlineData(75, null, "Warning")]
    [InlineData(89, "normal", "Warning")]
    [InlineData(90, null, "Critical")]
    // The CLI's own verdict counts even under the thresholds...
    [InlineData(10, "warning", "Warning")]
    [InlineData(10, "rejected", "Critical")]
    // ...including one this build has no name for.
    [InlineData(10, "something_new", "Warning")]
    public void Level_takes_the_worse_of_the_thresholds_and_the_severity(int percent, string severity, string expected)
        => Assert.Equal(expected, UsageStatusFormat.LevelOf(percent, severity).ToString());

    [Theory]
    [InlineData("2026-09-13T20:09:59.845282+00:00", "resets 20:09 (in 2h 31m)")]
    [InlineData("2026-09-20T04:59:59.845306+00:00", "resets Sun 04:59 (in 6d 11h)")]
    [InlineData("2026-09-13T17:00:00+00:00", "resets soon")]
    [InlineData(null, "")]
    public void Reset_text_names_the_time_the_same_day_and_the_day_later(string resetsAt, string expected)
        => Assert.Equal(expected, UsageStatusFormat.ResetText(resetsAt, Now, TimeZoneInfo.Utc, Invariant));

    [Fact]
    public void Reset_text_is_in_the_given_time_zone()
    {
        var plusThree = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");

        Assert.Equal("resets 23:09 (in 2h 31m)", UsageStatusFormat.ResetText("2026-09-13T20:09:59Z", Now, plusThree, Invariant));
    }

    [Theory]
    [InlineData(30, "1m")]
    [InlineData(45 * 60, "45m")]
    [InlineData((2 * 3600) + (31 * 60) + 59, "2h 31m")]
    [InlineData((6 * 86400) + (11 * 3600) + (22 * 60), "6d 11h")]
    public void Span_keeps_at_most_two_units(int seconds, string expected)
        => Assert.Equal(expected, UsageStatusFormat.Span(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_rate_limit_event_updates_its_window_in_the_views_units()
    {
        // The event speaks a 0..1 fraction and Unix seconds; the windows speak percent and ISO.
        var resets = new DateTimeOffset(2026, 9, 13, 20, 9, 59, TimeSpan.Zero);
        var windows = new[] { Window(UsageMapper.SessionKind, 44), Window(UsageMapper.WeeklyKind, 10) };

        var updated = UsageStatusFormat.ApplyRateLimit(windows, new RateLimitInfo
        {
            Status = "allowed_warning",
            RateLimitType = "five_hour",
            Utilization = 0.81,
            ResetsAt = resets.ToUnixTimeSeconds(),
        });

        Assert.Equal(81, updated[0].Utilization);
        Assert.Equal("warning", updated[0].Severity);
        Assert.Equal(resets, DateTimeOffset.Parse(updated[0].ResetsAt, Invariant));
        Assert.Equal(10, updated[1].Utilization);
        // The snapshot the UI holds is replaced, never edited in place.
        Assert.Equal(44, windows[0].Utilization);
    }

    [Fact]
    public void A_rejected_event_without_a_figure_reads_full()
    {
        var updated = UsageStatusFormat.ApplyRateLimit(
            new[] { Window(UsageMapper.WeeklyKind, 60) },
            new RateLimitInfo { Status = "rejected", RateLimitType = "seven_day" });

        var weekly = Assert.Single(updated);
        Assert.Equal(100, weekly.Utilization);
        Assert.Equal(UsageLevel.Critical, UsageStatusFormat.WindowLevel(weekly, Now));
    }

    [Theory]
    // Credit, not a window.
    [InlineData("overage")]
    // Scoped to a model the event doesn't name, so there is no telling which row it is.
    [InlineData("seven_day_opus")]
    [InlineData("")]
    public void Events_for_windows_the_bar_cannot_place_change_nothing(string type)
    {
        var windows = new[] { Window(UsageMapper.SessionKind, 44) };

        Assert.Same(windows, UsageStatusFormat.ApplyRateLimit(windows,
            new RateLimitInfo { Status = "allowed_warning", RateLimitType = type, Utilization = 0.9 }));
    }

    [Fact]
    public void An_event_for_a_window_not_fetched_yet_adds_it_in_order()
    {
        var updated = UsageStatusFormat.ApplyRateLimit(
            new[] { Window(UsageMapper.WeeklyKind, 10) },
            new RateLimitInfo { Status = "allowed", RateLimitType = "five_hour", Utilization = 0.3 });

        Assert.Equal(new[] { UsageMapper.SessionKind, UsageMapper.WeeklyKind }, updated.Select(w => w.Kind));
        Assert.Equal("Session (5hr)", updated[0].Name);
        Assert.Equal(30, updated[0].Utilization);
    }

    [Fact]
    public void An_event_with_no_figure_for_a_window_not_fetched_adds_nothing()
    {
        var none = new RateWindowDto[0];

        Assert.Same(none, UsageStatusFormat.ApplyRateLimit(none, new RateLimitInfo { Status = "allowed", RateLimitType = "five_hour" }));
    }

    [Theory]
    [InlineData("ANTHROPIC_BASE_URL", "https://api.z.ai/api/anthropic", true)]
    [InlineData("ANTHROPIC_API_KEY", "test-key", true)]
    // Env names are case-insensitive on Windows.
    [InlineData("anthropic_auth_token", "test-token", true)]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", "1", true)]
    [InlineData("CLAUDE_CODE_USE_BEDROCK", "0", false)]
    // A model override still logs in through claude.ai.
    [InlineData("ANTHROPIC_MODEL", "claude-opus-5", false)]
    [InlineData("ANTHROPIC_BASE_URL", "", false)]
    public void Profiles_routed_away_from_claude_ai_have_no_plan(string key, string value, bool expected)
        => Assert.Equal(expected, UsageStatusFormat.HasNoPlan(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void Tooltip_lists_every_window_with_its_reset_and_source()
    {
        var snapshot = Snapshot(
            Window(UsageMapper.SessionKind, 44, "2026-09-13T20:09:59Z"),
            Window(UsageMapper.WeeklyScopedKind, 13, "2026-09-20T04:59:59Z", name: "Weekly Fable"));

        var lines = UsageStatusFormat.Tooltip(snapshot, Now, TimeZoneInfo.Utc, Invariant)
            .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

        Assert.Equal(new[]
        {
            "Claude — Claude max",
            "Session (5hr): 44% · resets 20:09 (in 2h 31m)",
            "Weekly Fable: 13% · resets Sun 04:59 (in 6d 11h)",
            "Updated 17:38 · via Chat 1",
        }, lines);
    }

    [Fact]
    public void A_failed_refresh_keeps_the_last_numbers()
    {
        var stale = Snapshot(Window(UsageMapper.SessionKind, 44)).WithError("timeout", Now.AddMinutes(5));

        Assert.Equal(UsageAvailability.Available, stale.State);
        Assert.True(stale.IsStale);
        Assert.Equal("Claude: 5h 44%", UsageStatusFormat.StatusText(stale, Now));
    }

    [Fact]
    public void A_failure_with_nothing_earlier_is_unavailable()
        => Assert.Equal(UsageAvailability.Unavailable, UsageSnapshot.Initial("Claude").WithError("timeout", Now).State);

    [Fact]
    public void An_answer_without_limits_is_no_plan_not_a_failure()
    {
        Assert.Equal(UsageAvailability.NoPlan, UsageStatusFormat.Classify(new UsageDto { RateLimitsAvailable = false }));
        Assert.Equal(UsageAvailability.Unavailable, UsageStatusFormat.Classify(null));
    }

    [Fact]
    public void Next_reset_is_the_earliest_still_ahead()
    {
        var usage = new UsageDto
        {
            Windows = new[]
            {
                Window(UsageMapper.WeeklyKind, 10, "2026-09-20T04:59:59Z"),
                Window(UsageMapper.SessionKind, 44, "2026-09-13T20:09:59Z"),
                // Already past.
                Window(UsageMapper.WeeklyScopedKind, 13, "2026-09-13T17:00:00Z"),
            },
        };

        Assert.Equal(new DateTimeOffset(2026, 9, 13, 20, 9, 59, TimeSpan.Zero), UsageStatusFormat.NextReset(usage, Now));
    }
}

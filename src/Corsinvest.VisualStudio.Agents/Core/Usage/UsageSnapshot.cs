/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using System;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Where a profile's usage stands for the status bar. Only <see cref="Available"/> carries
/// numbers; the others say why there are none, which the item and its popup word differently.</summary>
internal enum UsageAvailability
{
    /// <summary>Nothing asked yet.</summary>
    Unknown,

    /// <summary>The CLI reported the plan's windows.</summary>
    Available,

    /// <summary>No plan limits apply: a third-party provider or an API key, or the CLI said so.</summary>
    NoPlan,

    /// <summary>Asking failed and there is nothing older to show.</summary>
    Unavailable,

    /// <summary>claude.exe could not be found, so there is nothing to ask.</summary>
    CliMissing,
}

/// <summary>One profile's usage as the status bar shows it: the last good answer, when and where it came
/// from, and the last failure if a later refresh went wrong. Never edited once published — every change
/// is a new instance, so the UI can hold one without locking.</summary>
internal sealed class UsageSnapshot
{
    private UsageSnapshot(string profileName, UsageAvailability state, UsageDto usage, DateTimeOffset? fetchedAt,
        string source, string lastError, DateTimeOffset? lastErrorAt, bool isFetching)
    {
        ProfileName = profileName ?? "";
        State = state;
        Usage = usage;
        FetchedAt = fetchedAt;
        Source = source;
        LastError = lastError;
        LastErrorAt = lastErrorAt;
        IsFetching = isFetching;
    }

    public string ProfileName { get; }

    public UsageAvailability State { get; }

    /// <summary>The last good answer; null until there is one.</summary>
    public UsageDto Usage { get; }

    public DateTimeOffset? FetchedAt { get; }

    /// <summary>Where the numbers came from, for the tooltip: a pane title, or "background".</summary>
    public string Source { get; }

    public string LastError { get; }

    public DateTimeOffset? LastErrorAt { get; }

    public bool IsFetching { get; }

    /// <summary>Numbers are shown, but the refresh after them failed.</summary>
    public bool IsStale => Usage != null && LastErrorAt != null;

    public static UsageSnapshot Initial(string profileName, UsageAvailability state = UsageAvailability.Unknown)
        => new(profileName, state, null, null, null, null, null, false);

    /// <summary>A fresh answer: replaces the numbers and forgets any earlier failure.</summary>
    public UsageSnapshot WithUsage(UsageDto usage, DateTimeOffset at, string source)
        => new(ProfileName, UsageStatusFormat.Classify(usage), usage, at, source, null, null, false);

    /// <summary>Windows changed by a rate_limit_event. Not a fetch, so <see cref="FetchedAt"/> stays and
    /// the next scheduled refresh still happens.</summary>
    public UsageSnapshot WithWindows(RateWindowDto[] windows)
    {
        var old = Usage ?? new UsageDto { Plan = "—" };
        var usage = new UsageDto
        {
            Account = old.Account,
            AuthMethod = old.AuthMethod,
            Plan = old.Plan,
            RateLimitsAvailable = true,
            Windows = windows,
            Day = old.Day,
            Week = old.Week,
        };
        return new(ProfileName, UsageStatusFormat.Classify(usage), usage, FetchedAt, Source, LastError, LastErrorAt, IsFetching);
    }

    /// <summary>A failed refresh: the last good numbers stay when there are some.</summary>
    public UsageSnapshot WithError(string error, DateTimeOffset at)
        => new(ProfileName, Usage == null ? UsageAvailability.Unavailable : State, Usage, FetchedAt, Source, error, at, false);

    public UsageSnapshot WithFetching(bool fetching)
        => new(ProfileName, State, Usage, FetchedAt, Source, LastError, LastErrorAt, fetching);
}

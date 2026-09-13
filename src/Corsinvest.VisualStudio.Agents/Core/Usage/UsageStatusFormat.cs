/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>How worried a window's bar should look.</summary>
internal enum UsageLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>One figure in the status bar item — "5h 44%" — with the level its bar is drawn at.</summary>
internal sealed class UsageSegment(string label, int percent, UsageLevel level)
{
    public string Label { get; } = label;

    public int Percent { get; } = percent;

    public UsageLevel Level { get; } = level;

    public string Text => $"{Label} {Percent}%";
}

/// <summary>What the status bar item and its popup say, worked out from a snapshot without any UI.
/// "Now", the time zone and the culture are parameters so the wording can be tested; the extension
/// passes DateTimeOffset.Now, TimeZoneInfo.Local and CultureInfo.CurrentCulture.</summary>
internal static class UsageStatusFormat
{
    /// <summary>From here the bar turns amber: a quarter of the window left.</summary>
    public const int WarningPercent = 75;

    /// <summary>From here it turns red: one long turn may not fit.</summary>
    public const int CriticalPercent = 90;

    // Env keys that point the CLI at something other than a claude.ai login. None of those sessions has
    // plan limits, so a probe would start a process only to learn that.
    private static readonly string[] NoPlanEnvKeys =
    {
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
    };

    /// <summary>Available when the CLI reported windows, NoPlan when it answered without any, Unavailable
    /// when there was no answer at all — the distinction the Usage tab doesn't need to make.</summary>
    public static UsageAvailability Classify(UsageDto usage)
        => usage == null ? UsageAvailability.Unavailable
            : usage.RateLimitsAvailable && usage.Windows?.Length > 0 ? UsageAvailability.Available
            : UsageAvailability.NoPlan;

    /// <summary>Whether a profile's env routes the CLI away from a claude.ai login.</summary>
    public static bool HasNoPlan(IEnumerable<KeyValuePair<string, string>> env)
        => env != null && env.Any(kv => NoPlanEnvKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase) && IsSet(kv.Value));

    public static RateWindowDto Find(UsageDto usage, string kind)
        => usage?.Windows?.FirstOrDefault(w => w.Kind == kind);

    /// <summary>The item's figures: the session window, then the weekly one, as far as the CLI reported
    /// them. Empty when there are no numbers to show.</summary>
    public static IReadOnlyList<UsageSegment> Segments(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var segments = new List<UsageSegment>(2);
        if (snapshot?.State != UsageAvailability.Available) { return segments; }
        AddSegment(segments, "5h", Find(snapshot.Usage, UsageMapper.SessionKind), now);
        AddSegment(segments, "7d", Find(snapshot.Usage, UsageMapper.WeeklyKind), now);
        return segments;
    }

    /// <summary>"Claude: 5h 44% · 7d 10%"; the bare profile name while there is nothing to show, and a dash
    /// after it when asking failed with no earlier numbers to fall back on.</summary>
    public static string StatusText(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var name = snapshot?.ProfileName ?? "";
        var segments = Segments(snapshot, now);
        if (segments.Count > 0) { return $"{name}: {string.Join(" · ", segments.Select(s => s.Text))}"; }
        return snapshot?.State == UsageAvailability.Unavailable ? $"{name}: —" : name;
    }

    /// <summary>Whether the window's reset time has passed: its figure then describes a window that is
    /// over.</summary>
    public static bool IsExpired(RateWindowDto window, DateTimeOffset now)
        => TryParseIso(window?.ResetsAt, out var resetsAt) && resetsAt <= now;

    /// <summary>The utilization as of now: 0 once the window has reset, until a refresh says otherwise.</summary>
    public static int EffectivePercent(RateWindowDto window, DateTimeOffset now)
        => window == null || IsExpired(window, now) ? 0 : window.Utilization;

    public static UsageLevel WindowLevel(RateWindowDto window, DateTimeOffset now)
        => window == null || IsExpired(window, now) ? UsageLevel.Normal : LevelOf(window.Utilization, window.Severity);

    /// <summary>The worse of what the thresholds say and what the CLI said.</summary>
    public static UsageLevel LevelOf(int percent, string severity)
    {
        var byPercent = percent >= CriticalPercent ? UsageLevel.Critical
            : percent >= WarningPercent ? UsageLevel.Warning
            : UsageLevel.Normal;
        var bySeverity = severity switch
        {
            null or "" or "normal" => UsageLevel.Normal,
            "critical" or "exceeded" or "blocked" or "rejected" => UsageLevel.Critical,
            // Anything else the CLI flags is worth a look, even a verdict this build has no name for.
            _ => UsageLevel.Warning,
        };
        return (UsageLevel)Math.Max((int)byPercent, (int)bySeverity);
    }

    /// <summary>"resets 20:09 (in 2h 31m)" the same day, "resets Sun 04:59 (in 6d 11h)" later; empty when
    /// the CLI gave no reset time, "resets soon" once it has passed.</summary>
    public static string ResetText(string resetsAtIso, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (!TryParseIso(resetsAtIso, out var resetsAt)) { return ""; }
        var left = resetsAt - now;
        if (left <= TimeSpan.Zero) { return "resets soon"; }
        var local = TimeZoneInfo.ConvertTime(resetsAt, zone);
        var time = Clock(resetsAt, zone, culture);
        var when = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? time : $"{local.ToString("ddd", culture)} {time}";
        return $"resets {when} (in {Span(left)})";
    }

    /// <summary>"45m", "2h 31m", "6d 11h": two units at most, which is all a reset needs.</summary>
    public static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1) { return $"{(int)span.TotalDays}d {span.Hours}h"; }
        if (span.TotalHours >= 1) { return $"{(int)span.TotalHours}h {span.Minutes}m"; }
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
    }

    /// <summary>"Updated 17:40 · via Chat 2"; empty before the first answer.</summary>
    public static string UpdatedText(UsageSnapshot snapshot, TimeZoneInfo zone, CultureInfo culture)
    {
        if (snapshot?.FetchedAt is not DateTimeOffset at) { return ""; }
        var text = "Updated " + Clock(at, zone, culture);
        return string.IsNullOrEmpty(snapshot.Source) ? text : $"{text} · via {snapshot.Source}";
    }

    /// <summary>The item's tooltip: the plan, every window with its reset, and where the numbers came from.</summary>
    public static string Tooltip(UsageSnapshot snapshot, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (snapshot == null) { return ""; }
        var sb = new StringBuilder(snapshot.ProfileName);
        switch (snapshot.State)
        {
            case UsageAvailability.Available:
                var usage = snapshot.Usage;
                if (!string.IsNullOrEmpty(usage.Plan) && usage.Plan != "—") { sb.Append(" — ").Append(usage.Plan); }
                foreach (var w in usage.Windows ?? [])
                {
                    sb.AppendLine().Append(w.Name).Append(": ").Append(EffectivePercent(w, now)).Append('%');
                    var reset = ResetText(w.ResetsAt, now, zone, culture);
                    if (reset.Length > 0) { sb.Append(" · ").Append(reset); }
                }
                break;
            case UsageAvailability.NoPlan:
                sb.AppendLine().Append("No plan limits for this profile");
                break;
            case UsageAvailability.Unavailable:
                sb.AppendLine().Append("Usage unavailable");
                break;
            case UsageAvailability.CliMissing:
                sb.AppendLine().Append("Claude Code CLI not found");
                break;
            default:
                sb.AppendLine().Append(snapshot.IsFetching ? "Fetching usage…" : "Click to fetch usage");
                break;
        }
        var updated = UpdatedText(snapshot, zone, culture);
        if (updated.Length > 0) { sb.AppendLine().Append(updated); }
        if (snapshot.LastErrorAt is DateTimeOffset failedAt)
        {
            sb.AppendLine().Append("Last refresh failed at ").Append(Clock(failedAt, zone, culture));
            if (!string.IsNullOrEmpty(snapshot.LastError)) { sb.Append(": ").Append(snapshot.LastError); }
        }
        return sb.ToString();
    }

    /// <summary>The earliest reset still ahead, so a refresh can land just after a window rolls over.</summary>
    public static DateTimeOffset? NextReset(UsageDto usage, DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var w in usage?.Windows ?? [])
        {
            if (TryParseIso(w.ResetsAt, out var resetsAt) && resetsAt > now && (next == null || resetsAt < next))
            {
                next = resetsAt;
            }
        }
        return next;
    }

    /// <summary>Folds a rate_limit_event into the windows already shown. The event covers one window in
    /// its own units — a 0..1 fraction and Unix seconds — so it is converted here. Returns the input
    /// itself when the event names nothing this can place; never edits it.</summary>
    public static RateWindowDto[] ApplyRateLimit(RateWindowDto[] windows, RateLimitInfo info)
    {
        windows ??= [];
        var kind = UsageMapper.NormalizeKind(info?.RateLimitType);
        // Overage is credit, not a window; a model-scoped weekly can't be matched to its row from the event.
        if (kind is not (UsageMapper.SessionKind or UsageMapper.WeeklyKind)) { return windows; }

        var index = Array.FindIndex(windows, w => w.Kind == kind);
        var current = index >= 0 ? windows[index] : null;
        int? percent = info.Utilization is double fraction ? (int)Math.Round(fraction * 100) : null;
        if (percent == null && info.Status == "rejected") { percent = 100; }
        if (percent == null && current == null) { return windows; }

        var patched = new RateWindowDto
        {
            Kind = kind,
            Name = current?.Name ?? UsageMapper.WindowName(kind),
            Utilization = Math.Max(0, Math.Min(100, percent ?? current.Utilization)),
            ResetsAt = info.ResetsAt is long seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("o", CultureInfo.InvariantCulture)
                : current?.ResetsAt,
            Severity = info.Status switch
            {
                "rejected" => "rejected",
                "allowed_warning" => "warning",
                _ => null,
            },
        };
        var result = new List<RateWindowDto>(windows);
        if (index >= 0) { result[index] = patched; }
        else { result.Add(patched); }
        return [.. result.OrderBy(w => UsageMapper.KindOrder(w.Kind))];
    }

    private static void AddSegment(List<UsageSegment> segments, string label, RateWindowDto window, DateTimeOffset now)
    {
        if (window == null) { return; }
        segments.Add(new UsageSegment(label, EffectivePercent(window, now), WindowLevel(window, now)));
    }

    private static string Clock(DateTimeOffset at, TimeZoneInfo zone, CultureInfo culture)
        => TimeZoneInfo.ConvertTime(at, zone).ToString(culture.DateTimeFormat.ShortTimePattern, culture);

    // "0" and "false" are how a switch like CLAUDE_CODE_USE_BEDROCK is turned off without deleting it.
    private static bool IsSet(string value)
        => !string.IsNullOrWhiteSpace(value) && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseIso(string iso, out DateTimeOffset value)
    {
        value = default;
        return !string.IsNullOrEmpty(iso)
            && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
}

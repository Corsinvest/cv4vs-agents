/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Turns the CLI's experimental get_usage payload (+ the account from init) into the typed
/// <see cref="UsageDto"/>. Read defensively — every field is optional, a missing one falls back
/// rather than throwing. Mirrors the WebView dialog's wording (cv-usage-dialog) so both views match.</summary>
internal static class UsageMapper
{
    // The per-key windows older CLIs report, and their labels, same wording as the WebView dialog.
    private static readonly (string Key, string Name)[] KnownWindows =
    {
        ("five_hour", "Session (5hr)"),
        ("seven_day", "Weekly (7 day)"),
        ("seven_day_opus", "Weekly Opus"),
        ("seven_day_sonnet", "Weekly Sonnet"),
    };

    public static UsageDto Build(JObject raw, AccountDto account)
    {
        var dto = new UsageDto { Account = account, AuthMethod = AuthLabel(account?.ApiProvider) };

        var sub = raw?.Val("subscription_type") ?? account?.SubscriptionType;
        dto.Plan = string.IsNullOrEmpty(sub) ? "—" : "Claude " + sub;

        if (raw?["rate_limits"] is JObject limits)
        {
            dto.RateLimitsAvailable = raw.Val("rate_limits_available", true);
            // The normalized list is what the CLI's own /usage draws, and the only place a weekly limit
            // scoped to one model appears. Older CLIs send just the per-key windows.
            var windows = limits["limits"] is JArray list ? FromLimitsList(list) : FromKnownWindows(limits);
            dto.Windows = [.. windows
                .OrderBy(w => KindOrder(w.Kind))
                .ThenBy(w => w.Window.Name, StringComparer.OrdinalIgnoreCase)
                .Select(w => w.Window)];
        }

        if (raw?["behaviors"] is JObject behaviors)
        {
            dto.Day = BuildBehaviors(behaviors["day"] as JObject);
            dto.Week = BuildBehaviors(behaviors["week"] as JObject);
        }
        return dto;
    }

    private static IEnumerable<(string Kind, RateWindowDto Window)> FromLimitsList(JArray list)
        => list.OfType<JObject>().Select(limit =>
        {
            var kind = limit.Val("kind", "");
            // scope is a JSON null on the unscoped windows, and a null token can't be indexed into.
            var model = ((limit["scope"] as JObject)?["model"] as JObject)?.Val("display_name");
            return (kind, new RateWindowDto
            {
                Name = LimitName(kind, model),
                Utilization = Clamp(limit.Val("percent", 0)),
                ResetsAt = ReadIso(limit, "resets_at"),
            });
        });

    private static IEnumerable<(string Kind, RateWindowDto Window)> FromKnownWindows(JObject limits)
    {
        foreach (var (key, name) in KnownWindows)
        {
            if (limits[key] is JObject w)
            {
                yield return (key, new RateWindowDto
                {
                    Name = name,
                    Utilization = Clamp(w.Val("utilization", 0)),
                    ResetsAt = ReadIso(w, "resets_at"),
                });
            }
        }
    }

    // The list's labels, worded like the per-key ones; a scoped weekly is named after its model.
    private static string LimitName(string kind, string model) => kind switch
    {
        "session" => "Session (5hr)",
        "weekly_all" => "Weekly (7 day)",
        "weekly_scoped" => string.IsNullOrEmpty(model) ? "Weekly (one model)" : "Weekly " + model,
        _ => string.IsNullOrEmpty(kind) ? "Limit" : char.ToUpperInvariant(kind[0]) + kind.Substring(1).Replace('_', ' '),
    };

    // Session first, then the weekly limit, then the per-model ones, then anything newer — in either
    // shape's names.
    private static int KindOrder(string kind) => kind switch
    {
        "session" or "five_hour" => 0,
        "weekly_all" or "seven_day" => 1,
        "weekly_scoped" or "seven_day_opus" or "seven_day_sonnet" => 2,
        _ => 3,
    };

    private static int Clamp(int percent) => Math.Max(0, Math.Min(100, percent));

    // An ISO time as the views take it. The transport reads every line with JObject.Parse, which turns
    // such a string into a Date token whose plain string form is local time with no offset — and
    // ResetsIn below parses that as UTC. Round-tripping the token keeps the offset.
    private static string ReadIso(JObject o, string key) => (o[key] as JValue)?.Value switch
    {
        string s => s,
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static UsageBehaviorsDto BuildBehaviors(JObject period)
    {
        if (period == null) { return null; }
        return new UsageBehaviorsDto
        {
            // Only insights we have copy for are kept; the headline/body are composed here so the
            // clients just render them.
            Insights = [.. (period["behaviors"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(b => InsightCopy(b.Val("key", ""), b.Val("pct", 0)))
                .Where(c => c != null)
                .Select(c => new UsageInsightDto { Headline = c.Value.Headline, Body = c.Value.Body })],
            Skills = Attribution(period["skills"]),
            // The CLI sends subagents under "agents"; some builds also use "subagents".
            Subagents = Attribution(period["subagents"] ?? period["agents"]),
            Plugins = Attribution(period["plugins"]),
            McpServers = Attribution(period["mcp_servers"]),
        };
    }

    private static UsageAttributionDto[] Attribution(JToken arr)
        => [.. (arr as JArray ?? new JArray())
            .OfType<JObject>()
            .Select(x => new UsageAttributionDto { Name = x.Val("name", "—"), Pct = x.Val("pct", 0) })];

    // Headline + body for an insight key (pct fills the headline). Null for unknown keys → dropped.
    private static (string Headline, string Body)? InsightCopy(string key, int pct) => key switch
    {
        "long_context" => ($"{pct}% of your usage was at >150k context",
            "Longer sessions are more expensive even when cached. /compact mid-task, /clear when switching to new tasks."),
        "subagent_heavy" => ($"{pct}% of your usage came from subagent-heavy sessions",
            "Each subagent runs its own requests. Be deliberate about spawning them — and consider configuring a cheaper model for simpler subagents."),
        _ => null,
    };

    // Auth backend label (put into UsageDto.AuthMethod so clients don't re-decode apiProvider).
    private static string AuthLabel(string apiProvider) => apiProvider switch
    {
        "firstParty" => "Claude AI",
        "bedrock" => "Amazon Bedrock",
        "vertex" => "Google Vertex",
        "gateway" => "Enterprise gateway",
        _ => string.IsNullOrEmpty(apiProvider) ? "API key" : apiProvider,
    };

    /// <summary>Coarse "Resets in 3h / 4d" from an ISO timestamp (same wording as the dialog).
    /// Empty when unknown.</summary>
    public static string ResetsIn(string resetsAtIso)
    {
        if (string.IsNullOrEmpty(resetsAtIso)) { return ""; }
        if (!DateTimeOffset.TryParse(resetsAtIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            return "";
        }
        var ms = (dto.ToLocalTime() - DateTimeOffset.Now).TotalMilliseconds;
        if (ms <= 0) { return "Resets soon"; }
        var h = (int)Math.Round(ms / 3_600_000);
        return h < 24 ? $"Resets in {Math.Max(1, h)}h" : $"Resets in {(int)Math.Round(h / 24.0)}d";
    }
}

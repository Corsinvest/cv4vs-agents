/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Turns the CLI's experimental get_usage payload (+ the account from init) into the typed
/// <see cref="UsageDto"/>. Read defensively — every field is optional, a missing one falls back
/// rather than throwing. Mirrors the WebView dialog's wording (cv-usage-dialog) so both views match.</summary>
internal static class UsageMapper
{
    // The rate-limit windows and their labels, same wording as the WebView dialog.
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
            var windows = new List<RateWindowDto>();
            foreach (var (key, name) in KnownWindows)
            {
                if (limits[key] is JObject w)
                {
                    windows.Add(new RateWindowDto
                    {
                        Name = name,
                        Utilization = Math.Max(0, Math.Min(100, w.Val("utilization", 0))),
                        ResetsAt = w.Val("resets_at"),
                    });
                }
            }
            dto.Windows = windows.ToArray();
        }

        if (raw?["behaviors"] is JObject behaviors)
        {
            dto.Day = BuildBehaviors(behaviors["day"] as JObject);
            dto.Week = BuildBehaviors(behaviors["week"] as JObject);
        }
        return dto;
    }

    private static UsageBehaviorsDto BuildBehaviors(JObject period)
    {
        if (period == null) { return null; }
        return new UsageBehaviorsDto
        {
            // Only insights we have copy for are kept; the headline/body are composed here so the
            // clients just render them.
            Insights = (period["behaviors"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(b => InsightCopy(b.Val("key", ""), b.Val("pct", 0)))
                .Where(c => c != null)
                .Select(c => new UsageInsightDto { Headline = c.Value.Headline, Body = c.Value.Body })
                .ToArray(),
            Skills = Attribution(period["skills"]),
            // The CLI sends subagents under "agents"; some builds also use "subagents".
            Subagents = Attribution(period["subagents"] ?? period["agents"]),
            Plugins = Attribution(period["plugins"]),
            McpServers = Attribution(period["mcp_servers"]),
        };
    }

    private static UsageAttributionDto[] Attribution(JToken arr)
        => (arr as JArray ?? new JArray())
            .OfType<JObject>()
            .Select(x => new UsageAttributionDto { Name = x.Val("name", "—"), Pct = x.Val("pct", 0) })
            .ToArray();

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

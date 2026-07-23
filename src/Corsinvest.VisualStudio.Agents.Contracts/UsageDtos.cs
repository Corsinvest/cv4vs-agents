/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Contracts;

/// <summary>One rate-limit window from get_usage (the 5-hour / 7-day plan windows): a label,
/// utilization 0–100, and the ISO reset timestamp. resetsAt is null when the CLI didn't report
/// one.</summary>
public class RateWindowDto
{
    public string Name { get; set; }
    public int Utilization { get; set; }
    public string ResetsAt { get; set; }
}

/// <summary>An insight in the "what's contributing to your limits usage?" section — the ready-to-show
/// headline (with the % already filled in) and body advice. Composed on the C# side so clients don't
/// carry the copy.</summary>
public class UsageInsightDto
{
    public string Headline { get; set; }
    public string Body { get; set; }
}

/// <summary>One attribution row (a skill / subagent / plugin / MCP server) with its % of usage.</summary>
public class UsageAttributionDto
{
    public string Name { get; set; }
    public int Pct { get; set; }
}

/// <summary>The behaviours for one period (day or week): the insights + the per-category attribution
/// (skills / subagents / plugins / MCP servers).</summary>
public class UsageBehaviorsDto
{
    public UsageInsightDto[] Insights { get; set; } = System.Array.Empty<UsageInsightDto>();
    public UsageAttributionDto[] Skills { get; set; } = System.Array.Empty<UsageAttributionDto>();
    public UsageAttributionDto[] Subagents { get; set; } = System.Array.Empty<UsageAttributionDto>();
    public UsageAttributionDto[] Plugins { get; set; } = System.Array.Empty<UsageAttributionDto>();
    public UsageAttributionDto[] McpServers { get; set; } = System.Array.Empty<UsageAttributionDto>();

    public bool HasAttribution =>
        (Skills?.Length ?? 0) + (Subagents?.Length ?? 0) + (Plugins?.Length ?? 0) + (McpServers?.Length ?? 0) > 0;
}

/// <summary>The typed usage shown in the Account &amp; Usage view — parsed once on the C# side from
/// the CLI's experimental get_usage payload (+ the account fields from init), so both the WPF Usage
/// tab and the WebView dialog can render it without each re-parsing the raw.</summary>
public class UsageDto
{
    public AccountDto Account { get; set; }
    public string AuthMethod { get; set; } // apiProvider decoded to a human label
    public string Plan { get; set; }
    public bool RateLimitsAvailable { get; set; }
    public RateWindowDto[] Windows { get; set; } = System.Array.Empty<RateWindowDto>();
    public UsageBehaviorsDto Day { get; set; }
    public UsageBehaviorsDto Week { get; set; }
}

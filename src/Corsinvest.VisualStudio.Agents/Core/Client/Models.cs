/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

// Data the CLI reports about itself — the client's own shapes, deliberately NOT the generated
// Contracts DTOs: those describe what we send to the WebView, and this layer talks to the process,
// not to the UI. The pane maps between the two. Event payloads live in Events.cs.

/// <summary>Logged-in account, from the `initialize` reply.</summary>
public sealed class AccountInfo
{
    public string Email { get; set; }
    public string Organization { get; set; }
    public string SubscriptionType { get; set; }
    public string ApiProvider { get; set; }
}

/// <summary>Connection state of a single MCP (Model Context Protocol) server.</summary>
public sealed class McpServerStatus
{
    public string Name { get; set; }

    /// <summary>One of "connected", "failed", "needs-auth", "pending", "disabled".</summary>
    public string Status { get; set; }

    public string Error { get; set; }

    /// <summary>Configuration scope (project / user / local / claudeai / managed).</summary>
    public string Scope { get; set; }
}

/// <summary>Aggregate status of all configured MCP servers.</summary>
public sealed class McpStatus
{
    public List<McpServerStatus> Servers { get; set; } = [];
}

/// <summary>Spinner verbs the CLI suggests for the thinking indicator (`effective.spinnerVerbs`).</summary>
public sealed class SpinnerVerbs
{
    /// <summary>"append" (add to ours) or "replace" (use only these).</summary>
    public string Mode { get; set; }

    public string[] Verbs { get; set; } = [];
}

/// <summary>Per-model totals of a finished turn (`result.modelUsage`, keyed by model id). Cost is
/// the CLI's own figure — we never compute it, so a provider with its own pricing stays correct.
/// NOT the same shape as `message.usage`, which is snake_case and carries the four token counts
/// only: the wire is inconsistent here on purpose, so the two do not share a type.</summary>
public sealed class ModelUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadInputTokens { get; set; }
    public int CacheCreationInputTokens { get; set; }
    public int WebSearchRequests { get; set; }
    public double CostUsd { get; set; }
    public int ContextWindow { get; set; }
    public int MaxOutputTokens { get; set; }
}

/// <summary>Subscription rate-limit state (`rate_limit_event`). Only claude.ai plans emit it —
/// an API-key or 3P provider session never sees one.</summary>
public sealed class RateLimitInfo
{
    /// <summary>"allowed", "allowed_warning" or "rejected".</summary>
    public string Status { get; set; }

    /// <summary>Unix seconds when the window resets. Nullable on purpose: the banner says nothing
    /// about a reset it wasn't told about, which is not the same as one at epoch 0.</summary>
    public long? ResetsAt { get; set; }

    /// <summary>Which window: "five_hour", "seven_day", "seven_day_opus", … Open string on purpose:
    /// the CLI adds plan types, and an unknown one must not break the banner.</summary>
    public string RateLimitType { get; set; }

    /// <summary>Fraction of the window consumed (0..1). Null when unreported — distinct from 0%.</summary>
    public double? Utilization { get; set; }
}

/// <summary>A slash command the CLI advertises (built-in, skill or plugin), from `initialize`.</summary>
public sealed class SlashCommand
{
    /// <summary>Command name without the leading slash.</summary>
    public string Name { get; set; }

    public string Description { get; set; }

    /// <summary>Argument hint, e.g. "&lt;file&gt;". Empty when the command takes none.</summary>
    public string ArgumentHint { get; set; }

    /// <summary>Alternate names resolving to this command (/cost and /stats both reach /usage).</summary>
    public string[] Aliases { get; set; } = [];
}

/// <summary>One selectable model, as the CLI advertises it (`initialize` and `list_models` carry
/// the same rows). The catalogue is never a static table on our side: the CLI derives it from the
/// account, the API provider and the settings cascade, so alternative providers work unchanged.</summary>
public sealed class ModelInfo
{
    /// <summary>Catalogue key to pass back to `set_model` (e.g. "default", "opus[1m]", "sonnet").</summary>
    public string Value { get; set; }

    /// <summary>The served model id this entry maps to (e.g. "claude-opus-5[1m]"). Several entries
    /// can share one — "default" is usually an alias of a named row.</summary>
    public string ResolvedModel { get; set; }

    public string DisplayName { get; set; }
    public string Description { get; set; }

    public bool SupportsEffort { get; set; }
    public string[] SupportedEffortLevels { get; set; } = [];
    public bool SupportsAdaptiveThinking { get; set; }
    public bool SupportsFastMode { get; set; }
    public bool SupportsAutoMode { get; set; }
}

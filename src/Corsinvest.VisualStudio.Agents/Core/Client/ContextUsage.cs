/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Core.Client;

public sealed class ContextUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreateTokens { get; set; }
}

public sealed class ToolPermissionResponse
{
    public bool Allow { get; set; }
    public string DenyMessage { get; set; }

    /// <summary>
    /// Optional input replacement sent as <c>updatedInput</c> with <c>behavior:"allow"</c>;
    /// used by interactive tools (e.g. <c>AskUserQuestion</c>). Null = plain allow.
    /// </summary>
    public Newtonsoft.Json.Linq.JObject UpdatedInput { get; set; }

    /// <summary>
    /// Permission rules to apply with the allow (the CLI's <c>updatedPermissions</c>).
    /// Carries a chosen <c>permission_suggestion</c> when the user picks
    /// "allow … for this session". Null/empty = a plain one-time allow.
    /// </summary>
    public Newtonsoft.Json.Linq.JArray UpdatedPermissions { get; set; }
}

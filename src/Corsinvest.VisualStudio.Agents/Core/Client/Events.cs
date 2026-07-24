/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

public sealed class AccountInfo
{
    public string Email { get; set; }
    public string Organization { get; set; }
    public string SubscriptionType { get; set; }
    public string ApiProvider { get; set; }
}

public sealed class InitializedEventArgs
{
    public string SessionId { get; set; }
    public string WorkingDirectory { get; set; }
    public string Model { get; set; }
    public string PermissionMode { get; set; }
    // Runtime fast-mode state from system/init (off|cooldown|on). Not a persisted setting.
    public string FastModeState { get; set; }
}

/// <summary>The CLI's full startup state, gathered from `initialize` (fast_mode_state) + `get_settings`
/// (model + toggles) right after StartProcess — WITHOUT a user turn (system/init only arrives on the
/// first turn, so it can't seed the UI on open). Raw values (strings/JObject); the pane maps them to
/// the webview DTO and adds PermissionMode (which the CLI doesn't report — we pass it via
/// --permission-mode). Fired on every startup (open + respawn). Fields are null when get_settings fails.</summary>
public sealed class CliStateReceivedEventArgs
{
    // From get_settings.applied.model (resume = the session's own model, new = the settings default,
    // already resolved to a served id). Empty/null → the webview shows "Default".
    public string Model { get; set; }
    // The permission mode WE passed at launch (--permission-mode from Options/.jsonl). The CLI doesn't
    // report it, so the client captures its own value at startup — carried here so a rapid respawn
    // can't swap _client.PermissionMode out from under a late-firing event.
    public string PermissionMode { get; set; }
    // applied.effort (post-model-gate) ?? effective.effortLevel. Raw string ("low"|"medium"|"high"|"xhigh").
    public string EffortLevel { get; set; }
    public bool? AlwaysThinkingEnabled { get; set; }   // effective.alwaysThinkingEnabled
    public bool? Ultracode { get; set; }               // applied.ultracode
    public bool? SwitchModelsOnFlag { get; set; }      // effective.switchModelsOnFlag (absent in CLI → null → webview default)
    // effective.spinnerVerbs ({ mode, verbs }) or null. Raw JObject — the pane builds the DTO.
    public JObject SpinnerVerbs { get; set; }
    // From initialize.fast_mode_state (present only if fast is available for the account/org) or "off".
    public string FastModeState { get; set; }
}

public sealed class AssistantMessageEventArgs
{
    public string Model { get; set; }
    public string StopReason { get; set; }
    public JToken Content { get; set; }
    public JObject Usage { get; set; }
    public string Uuid { get; set; }
    /// <summary>Message time (epoch ms) parsed from the wire `timestamp`; null when absent.</summary>
    public long? Timestamp { get; set; }
    /// <summary>When non-null, this assistant message was emitted by a sub-agent
    /// run inside the given parent tool_use (Agent / Skill).</summary>
    public string ParentToolUseId { get; set; }
}

public sealed class UserMessageEventArgs
{
    public JToken Content { get; set; }
    public string Uuid { get; set; }
    /// <summary>Message time (epoch ms) parsed from the wire `timestamp`; null when absent.</summary>
    public long? Timestamp { get; set; }
    public JObject ToolUseResult { get; set; }
    /// <summary>When non-null, this user (tool_result) message was emitted by a
    /// sub-agent run inside the given parent tool_use (Agent / Skill).</summary>
    public string ParentToolUseId { get; set; }
    /// <summary>True for CLI-injected meta entries (e.g. local-command-caveat
    /// disclaimers preceding slash commands). Should be skipped from chat UI.</summary>
    public bool IsMeta { get; set; }
}

public sealed class AssistantTextDeltaEventArgs
{
    /// <summary>The newly streamed text chunk (to be appended to the in-flight assistant block).</summary>
    public string Delta { get; set; }
    /// <summary>Index of the content block this delta belongs to (0-based).</summary>
    public int Index { get; set; }
    /// <summary>When non-null, the delta is from a sub-agent under this tool_use.</summary>
    public string ParentToolUseId { get; set; }
}

public sealed class AssistantThinkingDeltaEventArgs
{
    public string Delta { get; set; }
    public int Index { get; set; }
    // -1 when the delta carries no token estimate (only some thinking_delta frames do).
    public int EstimatedTokens { get; set; }
    public string ParentToolUseId { get; set; }
}

public sealed class ToolProgressEventArgs
{
    public string ToolUseId { get; set; }
    public string ToolName { get; set; }
    public int ElapsedSeconds { get; set; }
    public string ParentToolUseId { get; set; }
    public string TaskId { get; set; }
}

public sealed class ResultEventArgs
{
    public string Subtype { get; set; }
    public int DurationMs { get; set; }
    public int DurationApiMs { get; set; }
    public bool IsError { get; set; }
    public int NumTurns { get; set; }
    public double? TotalCostUsd { get; set; }
    public string StopReason { get; set; }
    public JObject Usage { get; set; }
    /// <summary>Per-model usage, keyed by model id; each carries contextWindow /
    /// maxOutputTokens. Source of the context-window limits. Null when absent.</summary>
    public JObject ModelUsage { get; set; }
    /// <summary>Why the turn failed, when IsError: `result` on a success-subtype result,
    /// otherwise the joined `errors[]`. Empty when the turn succeeded.</summary>
    public string ErrorText { get; set; } = "";
    /// <summary>Finer-grained failure cause than StopReason (max_turns, budget_exhausted,
    /// prompt_too_long, …). Optional on the wire; empty when absent.</summary>
    public string TerminalReason { get; set; } = "";
}

/// <summary>Catalogue from the <c>initialize</c> control response: models (with
/// effort levels + capability flags) and the rich slash commands (name +
/// description + argumentHint). The CLI's only source for both.</summary>
public sealed class ModelsReceivedEventArgs
{
    public JArray Models { get; set; }
    public JArray UnavailableModels { get; set; }
    public JArray Commands { get; set; }
}

public sealed class ToolPermissionRequestEventArgs
{
    public string RequestId { get; set; }
    public string ToolUseId { get; set; }
    public string ToolName { get; set; }
    public JObject Input { get; set; }
    public string BlockedPath { get; set; }

    /// <summary>The CLI's <c>permission_suggestions</c> (array of PermissionUpdate).
    /// Each becomes an extra "allow … for this session/project" choice in the
    /// banner; the chosen one is echoed back as updatedPermissions.</summary>
    public JArray PermissionSuggestions { get; set; }
}

/// <summary>The CLI cancelled a pending can_use_tool (its turn was interrupted/superseded).
/// The permission banner for this tool_use must be dismissed — no answer is expected anymore.</summary>
public sealed class ToolPermissionCancelledEventArgs
{
    public string ToolUseId { get; set; }
}

public sealed class HookCallbackEventArgs
{
    public string RequestId { get; set; }
    public string CallbackId { get; set; }
    public string ToolUseId { get; set; }
    public JToken Input { get; set; }
}

public sealed class RateLimitEventArgs
{
    public JObject RateLimitInfo { get; set; }
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
    public System.Collections.Generic.List<McpServerStatus> Servers { get; set; }
        = [];
}


public sealed class ProcessStartedEventArgs
{
    public int Pid { get; set; }
    public string WorkingDirectory { get; set; }
    public string SessionId { get; set; }
}

public sealed class ProcessExitedEventArgs
{
    public int ExitCode { get; set; }
    public bool Intentional { get; set; }
}

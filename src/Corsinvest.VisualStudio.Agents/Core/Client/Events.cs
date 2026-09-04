/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

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
/// first turn, so it can't seed the UI on open). The pane maps these onto the webview DTO and adds
/// PermissionMode (which the CLI doesn't report — we pass it via --permission-mode). Fired on every
/// startup (open + respawn). Fields are null when get_settings fails.</summary>
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
    // effective.permissions.disableBypassPermissionsMode == "disable": an org policy forbids the
    // bypass mode. Absent/get_settings failed → false, i.e. allowed: only the exact "disable" may
    // hide the mode, never a missing value.
    public bool? BypassPermissionsDisabled { get; set; }
    // effective.spinnerVerbs, or null when the CLI configures none.
    public SpinnerVerbs SpinnerVerbs { get; set; }
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
    /// <summary>Uuids of messages this one REPLACES (refusal-fallback supersede): they were already
    /// delivered to us and are no longer part of the conversation. Same job as the end-of-turn
    /// `retracted_message_uuids`, arriving early and per-message; the two are idempotent with each
    /// other, so acting on both is safe. Null when the message replaces nothing, which is nearly
    /// always.</summary>
    public string[] Supersedes { get; set; }
    /// <summary><para>
    /// Why the API call failed, when it did. An API failure doesn't arrive as an error on
    /// the wire: the CLI fabricates a synthetic assistant whose TEXT is the error
    /// (`utils/messages.ts:445-457`), so without this field it reads as an ordinary answer — which
    /// is why one turn can show up twice, once as a grey-dot reply and once as the red notice the
    /// `result` raises.
    /// </para>
    /// <para>
    /// A closed enum, not free text: `SDKAssistantMessageError` (`sdk.d.ts:2846`) is
    /// authentication_failed | oauth_org_not_allowed | billing_error | rate_limit | overloaded |
    /// invalid_request | model_not_found | server_error | unknown | max_output_tokens. So it says
    /// WHICH failure, not just that there was one — a notice can name the rate limit instead of
    /// saying "API error".
    /// </para>
    /// <para>
    /// The frame also carries `is_api_error_message`, which is NOT in the SDK types — emitted
    /// without a contract, so not something to build on.
    /// </para></summary>
    public string Error { get; set; }
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
    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; set; }
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
    /// <summary>Selectable models. Parsed here rather than passed as raw JSON so every consumer
    /// reads the same shape `list_models` returns (ClaudeClient.ParseModels builds both).</summary>
    public IReadOnlyList<ModelInfo> Models { get; set; }

    /// <summary>Models the account cannot use — shown greyed out, not hidden.</summary>
    public IReadOnlyList<ModelInfo> UnavailableModels { get; set; }

    /// <summary>Slash commands (built-in, skills, plugins). Re-published on every init, unlike the
    /// catalogue — they don't get dirtied by a --resume.</summary>
    public IReadOnlyList<SlashCommand> Commands { get; set; }
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
    public RateLimitInfo Info { get; set; }
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

/// <summary>`system/bridge_state` — Remote Control. <see cref="State"/> is one of
/// ready|connected|reconnecting|failed; <see cref="Detail"/> is the CLI's own readable cause
/// and is absent on ready/connected.</summary>
public sealed class BridgeStateEventArgs
{
    public string State { get; set; }
    public string Detail { get; set; }
    /// <summary>Which bridge is speaking. Absent on `ready` — the event precedes the response
    /// that carries the epoch.</summary>
    public int? BridgeEpoch { get; set; }
}

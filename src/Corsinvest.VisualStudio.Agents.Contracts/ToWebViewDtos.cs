/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Contracts;

/// <summary>Severity of a notice/message-bar (wire values lowercase).
/// Generated as a TS string-literal union.</summary>
public enum NoticeVariantDto
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Reasoning effort level (wire values lowercase). Generated as a TS union.</summary>
public enum EffortLevelDto
{
    Low,
    Medium,
    High,
    Xhigh,
}

// ToWebView wire DTOs (host C# → WebView) — single source of truth. The .ts interfaces
// are generated from these by TypeGen (see BridgeGenerationSpec). Plain POCOs, no TypeGen
// attributes here (the spec lists what to export), so the shape stays clean. Serialized
// camelCase on the wire (Newtonsoft CamelCasePropertyNamesContractResolver). The opposite
// direction (WebView → C#) lives in FromWebViewDtos.cs.

/// <summary>Token usage on an assistant message / exchange-ended payload.</summary>
public class ContextUsageDto
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
}

/// <summary>A selectable model in the model picker (chat_models).</summary>
public class ModelInfoDto
{
    public string Value { get; set; }
    // The real served model id this catalogue entry maps to (e.g. "claude-opus-4-8[1m]"). Used
    // to resolve a served id back to its entry — like the VS Code extension — instead of guessing
    // by family name, which is fragile with alternative providers (env-var remapped models).
    public string ResolvedModel { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public bool SupportsEffort { get; set; }
    public string[] SupportedEffortLevels { get; set; }
    public bool SupportsFastMode { get; set; }
    public bool SupportsAdaptiveThinking { get; set; }
    public bool SupportsAutoMode { get; set; }
    /// <summary>True for unavailable models (e.g. Fable): shown greyed, not selectable.</summary>
    public bool Disabled { get; set; }
}

/// <summary>A CLI/skill slash command with its metadata (chat_slash_commands).</summary>
public class SlashCommandDto
{
    public string Name { get; set; }
    public string Description { get; set; }
    public string ArgumentHint { get; set; }
    public string[] Aliases { get; set; }
}

/// <summary>CLI runtime state (part of ui_init.CliState, also re-sent standalone as
/// vs_settings). model/permissionMode come from system/init (resume: from the .jsonl);
/// the rest from get_settings. NOT VS Options.</summary>
public class CliStateDto
{
    public string Model { get; set; }
    public string PermissionMode { get; set; }
    public EffortLevelDto? EffortLevel { get; set; }
    public bool? AlwaysThinkingEnabled { get; set; }
    public bool? SwitchModelsOnFlag { get; set; }
    public bool? Ultracode { get; set; }
    // From init.fast_mode_state (off|cooldown|on). The webview derives only the on/off toggle
    // (on = state != "off"); cooldown is not currently surfaced distinctly. Replaces the
    // always-null FastMode bool from get_settings.
    public string FastModeState { get; set; }
    // Custom spinner verbs from get_settings.effective.spinnerVerbs (null unless set in settings.json).
    // CLI state, not a VS Option — moved here from VsOptionsDto.
    public SpinnerVerbsConfigDto SpinnerVerbsConfig { get; set; }
}

/// <summary>A file/dir suggestion for the @-mention picker (file_suggestions).</summary>
public class AtItemDto
{
    public string Name { get; set; }
    public string Path { get; set; }
    public string Dir { get; set; }
    public bool IsDir { get; set; }
}

/// <summary>Lazily-fetched image bytes for a stripped chat image block (chat_image_data).</summary>
public class GetImageResponse
{
    public string Uuid { get; set; }
    public int BlockIdx { get; set; }
    public string Base64 { get; set; }
    public string MediaType { get; set; }
}

/// <summary>Running usage of a sub-agent (nested in subagent_progress).</summary>
public class SubagentUsageDto
{
    public int TotalTokens { get; set; }
    public int ToolUses { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>A sub-agent started (subagent_started).</summary>
public class SubagentStartedNotification
{
    public string TaskId { get; set; }
    public string Description { get; set; }
    public string TaskType { get; set; }
    public string ToolUseId { get; set; }
}

/// <summary>A sub-agent's progress update (subagent_progress).</summary>
public class SubagentProgressNotification
{
    public string TaskId { get; set; }
    public string Description { get; set; }
    public string LastToolName { get; set; }
    public string Summary { get; set; }
    public string ToolUseId { get; set; }
    public SubagentUsageDto Usage { get; set; }
}

/// <summary>A sub-agent finished (subagent_ended).</summary>
public class SubagentEndedNotification
{
    public string TaskId { get; set; }
    public string Status { get; set; }
}

/// <summary>Context was compacted (chat_compacted): tokens before/after.</summary>
public class CompactedNotification
{
    public string Trigger { get; set; }
    public int PreTokens { get; set; }
    public string Uuid { get; set; } = "";
}

/// <summary>The CLI's transient work status (chat_status): the raw `system/status` value
/// ("compacting", or "" when it ends). The WebView maps known values to a spinner label
/// (e.g. compacting → "Compacting…"); unknown values fall back to the random working verb.</summary>
public class StatusNotification
{
    public string Status { get; set; } = "";

    /// <summary>Outcome of a compaction, when this status ends one: "success" or "failed".
    /// Empty on every other status.</summary>
    public string CompactResult { get; set; } = "";

    /// <summary>Why the compaction failed (only with CompactResult = "failed").</summary>
    public string CompactError { get; set; } = "";
}

/// <summary>The CLI process exited (cli_exited).</summary>
public class CliExitedNotification
{
    public int ExitCode { get; set; }
    public bool Intentional { get; set; }
}

/// <summary>The CLI cancelled a pending permission (chat_tool_permission_cancel): dismiss the
/// banner whose tool_use matches. No answer is sent back to the CLI (it aborted the request).</summary>
public class ToolPermissionCancelNotification
{
    public string ToolUseId { get; set; }
}

/// <summary>A tool call's result (chat_tool_result). result is preview-clipped;
/// fullLineCount is the untruncated non-empty line count for count-only renderers.</summary>
public class ToolResultNotification
{
    public string ToolUseId { get; set; }
    public string Result { get; set; }
    public bool IsError { get; set; }
    public string ParentToolUseId { get; set; }
    public string AgentId { get; set; }
    public int FullLineCount { get; set; }
}

/// <summary>A rate-limit notice for the composer banner (chat_rate_limit). severity is
/// absent on a clear (message null); present ("warning"/"error") otherwise.</summary>
public class RateLimitNotification
{
    public string Key { get; set; }
    public NoticeVariantDto? Severity { get; set; }
    public string Message { get; set; }
}

/// <summary>The active model changed (cli_model_changed).</summary>
public class ModelChangedNotification
{
    public string Model { get; set; }
}

/// <summary>The permission mode changed (cli_permission_mode_changed).</summary>
public class PermissionModeChangedNotification
{
    public string Mode { get; set; }
}

/// <summary>A CLI-level error to surface as an error bubble (cli_error).</summary>
public class CliErrorNotification
{
    public string Message { get; set; }
}

/// <summary>A lazily-fetched image placeholder in a user message (chat_user_text). The
/// heavy base64 is stripped host-side; (uuid, blockIdx) address it for on-demand fetch.</summary>
public class UserImageDto
{
    public string Uuid { get; set; }
    public int BlockIdx { get; set; }
    public string MediaType { get; set; }

    /// <summary>Tiny inline PNG preview (data-URI) for the attachment chip, so the image
    /// is visible without fetching the full bytes. Null when the thumbnail couldn't be
    /// built (unsupported codec / corrupt data) — the chip then shows its file-type icon.</summary>
    public string Preview { get; set; }
}

/// <summary>A lazily-fetched document placeholder in a user message (chat_user_text).</summary>
public class UserFileDto
{
    public string Name { get; set; }
    public string Uuid { get; set; }
    public int BlockIdx { get; set; }
}

/// <summary>A tool_use surfaced to the WebView (chat_tool_permission): renders the tool row
/// and, when NeedsPermission, drives the permission banner. Input is the tool's raw argument
/// object; PermissionSuggestions are the CLI's permission_suggestions echoed back verbatim as
/// updatedPermissions (opaque to us). Usage rides on the first block of the turn (gauge).</summary>
public class ToolPermissionNotification
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Preview { get; set; }
    // Raw tool-argument object (JObject on the wire); keys stay snake_case as the CLI
    // sends them (file_path, command, …), so this is NOT a camelCased Dictionary.
    public object Input { get; set; }
    public string ParentToolUseId { get; set; }
    public bool NeedsPermission { get; set; }
    public object[] PermissionSuggestions { get; set; }
    public ContextUsageDto Usage { get; set; }
}

/// <summary>A user message echoed to the transcript (chat_user_text): its text plus any
/// stripped image/document placeholders. Images/Files are null when there are none;
/// parentToolUseId is set for sub-agent tool-result echoes.</summary>
public class UserTextNotification
{
    public string Text { get; set; }
    public UserImageDto[] Images { get; set; }
    public UserFileDto[] Files { get; set; }
    public string ParentToolUseId { get; set; }
    public string Uuid { get; set; }
}

/// <summary>One replayed bridge event inside a history page: Type = the bridge msg name
/// (chat_tool_result, chat_assistant_text, chat_tool_permission, chat_user_text), Data =
/// the DTO. Same {type,data} shape as a live bridge message, accumulated instead of sent.</summary>
public class HistoryEventDto
{
    public string Type { get; set; }
    public object Data { get; set; }
}

/// <summary>A page of transcript history as the RESPONSE to a getHistory request
/// (chat_history, scroll-up). Prepend=true: the page goes above the current transcript.</summary>
public class GetHistoryResponse
{
    public HistoryEventDto[] Events { get; set; }
    public string SessionId { get; set; }
    public long OldestOffset { get; set; }
    public bool HasMore { get; set; }
    public bool Prepend { get; set; }
}

/// <summary>The host pushed a fresh history page unprompted (chat_history_loaded): sent on
/// session open/resume, CLI respawn, and settings-reload re-render. NOTIFICATION (no request),
/// so no Prepend — an unprompted load always replaces/appends, never prepends.</summary>
public class HistoryLoadedNotification
{
    public HistoryEventDto[] Events { get; set; }
    public string SessionId { get; set; }
    public long OldestOffset { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>A sub-agent's full transcript on expand (subagent_loaded), as typed events
/// each carrying parentToolUseId = the Agent's tool_use_id for nesting.</summary>
public class GetSubagentResponse
{
    public string AgentId { get; set; }
    public HistoryEventDto[] Events { get; set; }
    public bool HasMore { get; set; }
}

/// <summary>A compaction's full summary text on expand (compact_summary_result).</summary>
public class GetCompactSummaryResponse
{
    public string Uuid { get; set; } = "";
    public string Summary { get; set; } = "";
}

/// <summary>Prefill the composer textbox (ui_set_composer): the startup prompt.</summary>
public class SetComposerNotification
{
    public string Text { get; set; }
}

/// <summary>The ↑/↓ prompt history for a session (chat_prompt_history). The WebView
/// keeps prompts; sessionId gates stale updates (only apply if it matches).</summary>
public class PromptHistoryNotification
{
    public string SessionId { get; set; }
    public string[] Prompts { get; set; }
}

/// <summary>The CLI/skill slash-command catalogue (chat_slash_commands).</summary>
public class SlashCommandsNotification
{
    public SlashCommandDto[] Commands { get; set; }
}

/// <summary>A streamed assistant-text token (chat_assistant_text_delta). parentToolUseId
/// routes the delta into a sub-agent's streaming bubble ("" for the main turn).</summary>
public class AssistantTextDeltaNotification
{
    public string Text { get; set; }
    public int Index { get; set; }
    public string ParentToolUseId { get; set; }
}

/// <summary>A streamed thinking token (chat_thinking_delta). estimatedTokens is -1 when this
/// frame carried no estimate; a thinking_tokens system frame reuses this DTO with Text="" to
/// push the authoritative cumulative count. parentToolUseId keys the entry ("" for the main turn).</summary>
public class ThinkingDeltaNotification
{
    public string Uuid { get; set; }
    public string Text { get; set; }
    public int EstimatedTokens { get; set; }
    public string ParentToolUseId { get; set; }
}

/// <summary>The thinking block closed (chat_thinking_ended): the WebView flips the label to
/// "Thought for Xs". durationMs is computed WebView-side (first delta → ended), so it's always 0 here.</summary>
public class ThinkingEndedNotification
{
    public string Uuid { get; set; }
    public long DurationMs { get; set; }
    // Keys the WebView's thinking entry (stream deltas have no message uuid yet).
    public string ParentToolUseId { get; set; }
    // A redacted_thinking block is cipher-only: it arrives with NO preceding thinking_delta, so the
    // WebView must create the entry here (get-or-create) and render a static, text-less box.
    public bool Redacted { get; set; }
}

/// <summary>Elapsed-time tick for a running Bash/PowerShell tool (chat_tool_progress).</summary>
public class ToolProgressNotification
{
    public string ToolUseId { get; set; }
    public string ToolName { get; set; }
    public int ElapsedSeconds { get; set; }
    public string ParentToolUseId { get; set; }
}

/// <summary>VS theme flipped light/dark (ui_theme_changed): the WebView reskins.</summary>
public class ThemeChangedNotification
{
    public bool Dark { get; set; }
}

/// <summary>The @-mention file/dir suggestions (file_suggestions): wrapper over AtItemDto.</summary>
public class GetSuggestionsResponse
{
    public AtItemDto[] Items { get; set; }
}

/// <summary>The model catalogue from the CLI's initialize (chat_models): wrapper over ModelInfoDto.</summary>
public class ModelsNotification
{
    public ModelInfoDto[] Models { get; set; }
}

/// <summary>A full assistant-text block (chat_assistant_text). usage rides on the FIRST
/// block of the turn only (null after), so the gauge counts the exchange once.</summary>
public class AssistantTextNotification
{
    public string Text { get; set; }
    public string ParentToolUseId { get; set; }
    public ContextUsageDto Usage { get; set; }
}

/// <summary>Spinner-verb override config (nested in the init ui payload). mode is the
/// raw settings string ("append"/"replace"); the WebView narrows it.</summary>
public class SpinnerVerbsConfigDto
{
    public string Mode { get; set; }
    public string[] Verbs { get; set; }
}

/// <summary>The {config} block of the init payload: pane config the WebView boots with.
/// WorkingDirectory is always set host-side (?? ""). Model/PermissionMode live in CliStateDto —
/// they're CLI state, not pane config. Slash commands arrive over chat_slash_commands
/// (initialize catalogue / commands_changed), not here.</summary>
public class InitConfigDto
{
    public string WorkingDirectory { get; set; }
    // True when the host was built in DEBUG (developer running the extension under VS). Gates
    // dev-only diagnostics in the WebView (e.g. the raw work status on the spinner). Always false in Release.
    public bool InDev { get; set; }
}

/// <summary>The {vsOptions} block of the init payload: the VS-settings-driven UI state, single
/// source of truth for the WebView (no defaults duplicated in JS). Adding an option =
/// one field here.</summary>
public class VsOptionsDto
{
    public bool ShowCostAndDuration { get; set; }
    public int PreviewLines { get; set; }
    public int ChatFontSize { get; set; }
    public bool ShowRelativePaths { get; set; }
    public bool StickyUserMessages { get; set; }
    public bool ShowInlineToolErrors { get; set; }
    public bool UseCtrlEnterToSend { get; set; }
    public bool CompactOutputAskAnswers { get; set; }
    public bool AllowDangerouslySkipPermissions { get; set; }
    public int DiffContextLines { get; set; }
    public bool DiffIgnoreWhitespace { get; set; }
    public bool ShowOpenDiffInVsButton { get; set; }
    public string[] AllowedUploadExtensions { get; set; }
    public string AppVersion { get; set; }
    public string AppCopyright { get; set; }
    public bool PerfEnabled { get; set; }
    public int LogLevel { get; set; }
}

/// <summary>The init payload (ui_init): the WebView's whole boot state, all 3 categories in one
/// shot (config, CLI state, VS options). The gate populates it when ready — no loading
/// placeholder (the previous Loading/LoadingMessage fields were always false/null).</summary>
public class InitPayloadNotification
{
    public InitConfigDto Config { get; set; }
    public CliStateDto CliState { get; set; }
    public VsOptionsDto VsOptions { get; set; }
}

/// <summary>The signed-in account shown in the Account & Usage dialog (nested in chat_usage).</summary>
public class AccountDto
{
    public string Email { get; set; }
    public string Organization { get; set; }
    public string SubscriptionType { get; set; }
    public string ApiProvider { get; set; }
}

/// <summary>Account & usage data for the dialog (chat_usage). account is null when the CLI
/// reported none; usage is the CLI's raw experimental /usage object (opaque JObject, shape
/// not stable — kept untyped like ToolPermissionNotification.Input).</summary>
public class GetUsageResponse
{
    public AccountDto Account { get; set; }
    public object Usage { get; set; }
}

/// <summary>The editor selection/active file changed (ide_selection_changed). All fields
/// null/false/0 when there's no editor context. Drives the composer's IDE-context badge.</summary>
public class IdeContextNotification
{
    public string FilePath { get; set; }
    public string FileName { get; set; }
    public bool HasSelection { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

/// <summary>Turn finished (chat_exchange_ended): cost/duration for the result line,
/// plus the model's real context window / max output (0 = unknown, keep previous).
/// usage is null when the result carried none.</summary>
public class ExchangeEndedNotification
{
    public double CostUsd { get; set; }
    public long DurationMs { get; set; }
    public bool IsError { get; set; }
    public ContextUsageDto Usage { get; set; }
    public long ContextWindow { get; set; }
    public long MaxOutputTokens { get; set; }

    /// <summary>Why the turn failed (only with IsError): the CLI's own message. May be empty
    /// even on a failure — the label below is what always identifies the cause.</summary>
    public string ErrorText { get; set; } = "";

    /// <summary>Machine-readable failure cause for the notice label: `terminal_reason` when the
    /// CLI sends it (finer), else the result `subtype` (error_max_turns, …). Empty when none.</summary>
    public string ErrorKind { get; set; } = "";
}

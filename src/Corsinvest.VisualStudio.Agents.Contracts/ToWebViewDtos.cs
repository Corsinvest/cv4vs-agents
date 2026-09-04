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

/// <summary>Which notice stack a message belongs in (wire values lowercase). `Top` = session/system
/// scope, shown at the top of the chat; `Composer` = turn scope, shown above the composer. Each stack
/// listens on the same channel and keeps only its own position.</summary>
public enum NoticePositionDto
{
    Top,
    Composer,
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
    // to resolve a served id back to its entry instead of guessing by family name, which is
    // fragile with alternative providers (env-var remapped models).
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
    // effective.permissions.disableBypassPermissionsMode == "disable": an org policy forbids the
    // bypass mode, so the selector must not offer it.
    public bool? BypassPermissionsDisabled { get; set; }
    // From init.fast_mode_state (off|cooldown|on). The webview derives only the on/off toggle
    // (on = state != "off"); cooldown is not currently surfaced distinctly.
    public string FastModeState { get; set; }
    // Custom spinner verbs from get_settings.effective.spinnerVerbs (null unless set in settings.json).
    // CLI state, not a VS Option.
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

/// <summary>Which sub-agents are running in the background, as ids (background_tasks_changed).
/// <para>Ids only, deliberately: the rows themselves — description, tools, tokens, duration —
/// come from the task_started/task_progress pair and are already tracked. This says which of
/// those to file under "background", nothing more.</para>
/// <para>REPLACE semantics: the whole set every time, so the receiver swaps rather than merges.
/// Empty means none. The CLI emits nothing at startup, so an empty set is also the right state
/// after a restart.</para></summary>
public class BackgroundTasksNotification
{
    public string[] TaskIds { get; set; } = [];
}

/// <summary>A change to a sub-agent already being tracked (task_updated): a new status, or a
/// renamed description.
/// <para>A patch on the task the started/progress pair already built, not a new one. Fields the
/// CLI did not send stay null — the wire patch carries only what changed.</para>
/// <para>The patch also carries is_backgrounded, which is NOT taken: it only ever describes a
/// foreground task being pushed down, and the CLI's asynchronous agents are background from
/// birth, so it never arrives for them. BackgroundTasksNotification is what says which are.</para></summary>
public class SubagentUpdatedNotification
{
    public string TaskId { get; set; }
    public string Status { get; set; }
    public string Description { get; set; }
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

/// <summary>What an Agent run cost, from the totals the CLI writes on its tool_result. Absent while
/// it runs, and for an INTERRUPTED run — there the CLI reports no figures at all, so the row shows
/// none rather than a number that would understate what it spent.</summary>
public class AgentRunTotalsDto
{
    public long DurationMs { get; set; }
    public long Tokens { get; set; }
    public int ToolUses { get; set; }
}

/// <summary>One hunk of the patch the CLI computed when it applied an edit, verbatim from
/// toolUseResult.structuredPatch. Line numbers are the file's, which is the whole point: an
/// Edit's input carries only the two fragments, so a patch computed from those starts at 1.
/// `Lines` are unified-diff rows — first char '-', '+' or ' ', then the text.</summary>
public class PatchHunkDto
{
    public int OldStart { get; set; }
    public int OldLines { get; set; }
    public int NewStart { get; set; }
    public int NewLines { get; set; }
    public string[] Lines { get; set; }
}

/// <summary><para>
/// The fields only one tool family reads, grouped so adding another one touches this class
/// and its renderer instead of widening the notification, the entry, the host and two call sites.
/// Null when the tool reports none — which is most of them.
/// </para>
/// <para>
/// agentId and fullLineCount deliberately stay OUT: the first is routing (the transcript lookup and
/// the sub-agent fetch use it, not the renderer) and arrives at launch rather than at the end; the
/// second is computed for every tool_result and describes `result`, like isError.
/// </para></summary>
public class ToolResultExtrasDto
{
    public AgentRunTotalsDto AgentTotals { get; set; }
    public PatchHunkDto[] Patch { get; set; }
}

/// <summary>A tool call's result (chat_tool_result). result is preview-clipped;
/// fullLineCount is the untruncated non-empty line count for the count-only renderers.</summary>
public class ToolResultNotification
{
    public string ToolUseId { get; set; }
    public string Result { get; set; }
    public bool IsError { get; set; }
    public string ParentToolUseId { get; set; }
    // The sub-agent this row spawned — Agent.
    public string AgentId { get; set; }
    // Untruncated non-empty line count — the count-only renderers (Grep/Glob/WebSearch).
    public int FullLineCount { get; set; }
    // Per-tool fields; null for a tool that reports none.
    public ToolResultExtrasDto Extras { get; set; }
}

/// <summary>A rate-limit notice for the composer banner (chat_rate_limit). severity is
/// absent on a clear (message null); present ("warning"/"error") otherwise.</summary>
public class RateLimitNotification
{
    public string Key { get; set; }
    public NoticeVariantDto? Severity { get; set; }
    public string Message { get; set; }
}

/// <summary>A notice for one of the two notice stacks (chat_notice) — today CLI advisories
/// (system/informational). Key dedups repeats of the same advisory; severity maps the CLI's level;
/// position picks the stack (absent = top, i.e. session scope).</summary>
public class NoticeNotification
{
    public string Key { get; set; }
    public NoticeVariantDto? Severity { get; set; }
    public string Message { get; set; }
    public NoticePositionDto? Position { get; set; }
    /// <summary>Optional action button label (e.g. "View logs"). With ActionMessage, clicking it
    /// sends that bridge message back to the host.</summary>
    public string ActionLabel { get; set; }
    /// <summary>Bridge message name the action button sends (fromWebView), e.g.
    /// open_ide_output_window. Ignored without ActionLabel.</summary>
    public string ActionMessage { get; set; }
    /// <summary>True for a notice that must stay until the host clears it (a dead CLI process) —
    /// it isn't auto-dismissed even at info severity.</summary>
    public bool Sticky { get; set; }
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
    // Message time (epoch ms) from the .jsonl record / live event; null when absent. The WebView
    // shows it as "x ago" with an absolute date/time tooltip.
    public long? Timestamp { get; set; }
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

    /// <summary>Turn the IDE-context eye back on with the text: a prompt picked from the editor
    /// context menu is about the file it was picked in, and asking about this code outranks the
    /// session's standing preference.</summary>
    public bool EnableIdeContext { get; set; }

    /// <summary>Send the text instead of leaving it in the composer, for a prompt the user asked
    /// for by pressing a button of its own. Otherwise it is a pre-fill they can still edit.</summary>
    public bool Send { get; set; }
}

/// <summary>A keystroke the host claimed on the WebView's behalf (ui_host_key).
/// <para>WebView2CompositionControl renders through Windows.UI.Composition, and its
/// CoreWebView2CompositionController exposes SendMouseInput/SendPointerInput but no keyboard
/// equivalent — so the keys it drops never reach the browser, and WPF hands them to Visual Studio
/// instead. The pane claims those and forwards them here for the page to act on.</para></summary>
public class HostKeyNotification
{
    /// <summary>DOM <c>KeyboardEvent.key</c> name ("Home", "End", …), not the WPF enum, so the
    /// page compares against the same strings a real key event would carry.</summary>
    public string Key { get; set; } = "";

    public bool Ctrl { get; set; }
    public bool Shift { get; set; }
    public bool Alt { get; set; }
}

/// <summary>Files dropped on the pane, already read (ui_files_dropped). Same root cause as
/// <see cref="HostKeyNotification"/>: with no window of its own the browser never sees the drop, so
/// WPF claims it before Visual Studio opens the file in an editor.</summary>
public class FilesDroppedNotification
{
    public DroppedFile[] Files { get; set; } = [];
}

public class DroppedFile
{
    public string Name { get; set; } = "";
    public string Base64 { get; set; } = "";
    /// <summary>Carried because a File built in script has no type of its own, and both the chip
    /// and the CLI block shape are chosen from it.</summary>
    public string MediaType { get; set; } = "";
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

/// <summary>Remote Control state for the banner. <c>Status</c> is disconnected|connecting|connected|error.
/// <c>Url</c> carries the claude.ai session link and is set only on connected; <c>Detail</c> carries
/// the CLI's own message and is set only on error.</summary>
public class RemoteControlNotification
{
    public string Status { get; set; }
    public string Url { get; set; }
    public string Detail { get; set; }
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
    // The message's wire uuid, so an entry can be addressed after the fact. Every block of one
    // assistant message carries the same one — the CLI derives per-block uuids for its own
    // retraction lists, but what reaches us here is the message's. Always present: the CLI writes
    // it unconditionally on both assistant lanes and SDKAssistantMessage.uuid is non-optional.
    // The permission banner's synthetic message is the one caller that passes none, and it only
    // ever emits tool_use blocks — never the text block this rides on.
    public string Uuid { get; set; }
    // Why the API call failed, when it did — a closed enum from the CLI (overloaded, rate_limit,
    // authentication_failed, …). An API failure arrives as an assistant message whose TEXT is the
    // error, so without this the chat renders it as an ordinary answer, grey dot included. Null on
    // every normal message: the CLI omits the field rather than sending an empty one.
    public string Error { get; set; }
    public ContextUsageDto Usage { get; set; }
    // Message time (epoch ms) from the .jsonl record / live event; null when absent. The WebView
    // shows it as "x ago" with an absolute date/time tooltip.
    public long? Timestamp { get; set; }
}

/// <summary><para>
/// Messages the CLI retracted (chat_evict_messages): they were delivered to us but are no
/// longer part of the conversation, so the model does not have them. Leaving them on screen is what
/// makes the transcript diverge from the model's context — the user reads a partial answer and
/// reasons about it, while the model never saw it.
/// </para>
/// <para>
/// Matching is by uuid equality and nothing else, which is what makes it safe to apply blind: a
/// uuid naming nothing on screen removes nothing.
/// </para></summary>
public class EvictMessagesNotification
{
    public string[] Uuids { get; set; }
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
    public bool CollapseTools { get; set; }
    public int ChatFontSize { get; set; }
    public bool ShowRelativePaths { get; set; }
    public bool StickyUserMessages { get; set; }
    public bool ShowInlineToolErrors { get; set; }
    public bool UseCtrlEnterToSend { get; set; }
    public bool CompactOutputAskAnswers { get; set; }
    public bool AllowDangerouslySkipPermissions { get; set; }

    /// <summary>Whether the CLI is keeping file snapshots for this pane. What hides the Rewind
    /// command when it is not: a command that can only answer "nothing to restore" is worse than
    /// one that is not offered.</summary>
    public bool FileCheckpoints { get; set; }

    /// <summary>Whether the selected code itself rides along with the prompt, or only its file and
    /// line numbers. The host composes the tag either way — the WebView is told so the context chip
    /// can show WHICH of the two is going out.</summary>
    public bool SendSelectionText { get; set; }
    public string[] AllowedUploadExtensions { get; set; }
    public string[] ExtraLinkableExtensions { get; set; }
    public string AppVersion { get; set; }
    public string AppCopyright { get; set; }
    public bool PerfEnabled { get; set; }
    public int LogLevel { get; set; }
}

/// <summary>The init payload (ui_init): what the host knows on its own — pane config and VS
/// options. Sent as soon as the WebView is up, before any history, so the first rows already
/// have the working directory they need to shorten paths against.
/// <para>The CLI's own state travels separately, on cli_state: it is not available until
/// claude.exe has answered initialize + get_settings, seconds later, and this payload must not
/// wait for it.</para></summary>
public class InitPayloadNotification
{
    public InitConfigDto Config { get; set; }
    public VsOptionsDto VsOptions { get; set; }
}

/// <summary>The CLI's startup state (cli_state), from initialize + get_settings — model, effort,
/// toggles. Sent on every startup, so a respawn re-seeds the UI without re-sending pane config
/// and VS options that did not change.</summary>
public class CliStateNotification
{
    public CliStateDto CliState { get; set; }
}

/// <summary>The signed-in account shown in the Account & Usage dialog (nested in chat_usage).</summary>
public class AccountDto
{
    public string Email { get; set; }
    public string Organization { get; set; }
    public string SubscriptionType { get; set; }
    public string ApiProvider { get; set; }
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

/// <summary>Answer to a rewind request (rewind_result).
/// <para>The two calls answer differently, and the caller knows which it asked for. A dry run
/// reports what WOULD change — <see cref="FilesChanged"/>, <see cref="Insertions"/>,
/// <see cref="Deletions"/>. A real rewind carries only the outcome: observed on the wire as
/// <c>{"canRewind": true, "skippedLinks": 0}</c>, with the statistics absent. So a zero here after
/// a real rewind means "not reported", not "nothing changed".</para>
/// <para><see cref="CanRewind"/> false with a reason in <see cref="Error"/> is a normal answer, not
/// a failure: the session may have no checkpoint for that message, or file history may be off
/// altogether (in SDK mode the CLI keeps it only when started with it enabled).</para></summary>
public class RewindResultNotification
{
    public string MessageUuid { get; set; }
    public bool CanRewind { get; set; }

    /// <summary>Why not, when CanRewind is false — the CLI's own wording.</summary>
    public string Error { get; set; } = "";

    /// <summary>What the rewind would touch, when the CLI reported it (probe only). Absolute paths.
    /// Null when it said nothing about the diff.</summary>
    public string[] FilesChanged { get; set; }

    public int Insertions { get; set; }
    public int Deletions { get; set; }

    /// <summary>How many files the CLI did NOT restore because they are symlinks. A count, not the
    /// paths: the wire carries `"skippedLinks": 0`. The one case where a rewind is partial, so it
    /// has to be said rather than left to be discovered.</summary>
    public int SkippedLinks { get; set; }
}

/// <summary>Which messages a rewind could actually restore something for (rewind_points), read
/// from the snapshots the CLI recorded in the transcript. The picker lists these and leaves the
/// rest out: the CLI would accept any user message, but here a rewind only touches files, so a
/// turn that changed none of them is a row with nowhere to go.</summary>
public class RewindPointsNotification
{
    public string[] Uuids { get; set; }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Shared TypeScript interfaces. Intentionally light: we type only the
// pieces we touch, not every JSON shape the C# host can send.

import type { SubagentUsageDto } from './generated/SubagentUsageDto';
import type { ToolResultExtrasDto } from './generated/ToolResultExtrasDto';
// Imported, not just re-exported below: UserTextEcho extends it, and a re-export is not in scope.
import type { UserTextNotification } from './generated/UserTextNotification';

/**
 * The one empty array the render passes hand out, because a fresh `[]` is a new identity and Lit
 * compares properties by identity: a literal in a template marks its element dirty on EVERY pass,
 * so a single streaming delta re-rendered every message and every tool row in the transcript.
 * Frozen so a consumer that mutates its own prop can't reach the others through it.
 */
export const EMPTY: readonly never[] = Object.freeze([]);

export type Theme = 'dark' | 'light';

/** Reasoning effort level union ('low'|'medium'|'high'|'xhigh'), generated from C#. */
export type { EffortLevelDto } from './generated/EffortLevelDto';

/** Slider levels (low→max) and their labels, matching the VS Code extension. */
// Exported only so the derived EffortSliderLevel below has a value to read: unexported, the
// array is a value nothing consumes and no-unused-vars rejects it.
export const EFFORT_SLIDER_LEVELS = ['low', 'medium', 'high', 'xhigh', 'max'] as const;
export type EffortSliderLevel = (typeof EFFORT_SLIDER_LEVELS)[number];
const EFFORT_LEVEL_LABELS: Readonly<Record<EffortSliderLevel, string>> = {
    low: 'Low',
    medium: 'Medium',
    high: 'High',
    xhigh: 'Extra high',
    max: 'Max',
};

/** Slider-stop value for ultracode. Deliberately NOT a key of EFFORT_LEVEL_LABELS: ultracode is not
 *  an effort level the CLI accepts — it is effort=xhigh plus a separate flag, so putting it in the
 *  union would make a state representable that the wire has no value for. Lowercase because it is
 *  an identifier, not display text (VS Code keeps the same split). */
export const ULTRACODE_VALUE = 'ultracode';

/** Display text for ultracode, capitalised like every other effort label. */
const ULTRACODE_LABEL = 'Ultracode';

/** The one place that turns effort state into display text, so the composer chip and the menu's
 *  slider can never word it differently. `ultracode` is required, not defaulted: the flag is what
 *  separates ultracode from a plain xhigh (both store effortLevel='xhigh'), so every caller has to
 *  say which it means — the slider's own ultracode stop is recognised by value instead. Anything
 *  with no label of its own shows its raw value. */
export function effortLabel(level: string, ultracode: boolean): string {
    if (ultracode || level === ULTRACODE_VALUE) {
        return ULTRACODE_LABEL;
    }
    return EFFORT_LEVEL_LABELS[level as EffortSliderLevel] ?? level;
}

/** A model as reported by the CLI's `initialize` response (via `chat_models`).
 *  Shape generated from C# (Contracts.ModelInfoDto) by TypeGen — re-exported here.
 *  `supportedEffortLevels` is empty for models without effort (e.g. Haiku);
 *  `disabled` is true for unavailable_models (e.g. Fable): greyed, not selectable. */
export type { ModelInfoDto } from './generated/ModelInfoDto';

/** CLI-sourced settings (model, permission mode, effort, toggles, spinner verbs) — the
 *  single-source-of-truth category of the init payload, also re-applied via `cli_state_changed`.
 *  Generated from C# (Contracts.CliStateDto) by TypeGen — re-exported here. */
export type { CliStateDto } from './generated/CliStateDto';

/** A CLI/skill slash command with its metadata (`chat_slash_commands`).
 *  Generated from C# (Contracts.SlashCommandDto) by TypeGen — re-exported here.
 *  `aliases` are searchable alternate names (e.g. `/loop` ↔ `proactive`). */
export type { SlashCommandDto } from './generated/SlashCommandDto';

/** A file/dir suggestion for the @-mention picker (`file_suggestions`).
 *  Generated from C# (Contracts.AtItemDto) by TypeGen — re-exported here. */
export type { AtItemDto } from './generated/AtItemDto';

/** Lazily-fetched image bytes for a stripped chat image block (`chat_image_data`).
 *  Generated from C# (Contracts.ImageDataResponseDto) by TypeGen — re-exported here. */
export type { GetImageResponse } from './generated/GetImageResponse';
export type { GetImageRequest } from './generated/GetImageRequest';

/** Sub-agent lifecycle events (`subagent_started` / `_progress` / `_ended`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { SubagentStartedNotification } from './generated/SubagentStartedNotification';
export type { SubagentProgressNotification } from './generated/SubagentProgressNotification';
export type { SubagentEndedNotification } from './generated/SubagentEndedNotification';
export type { SubagentUpdatedNotification } from './generated/SubagentUpdatedNotification';
export type { BackgroundTasksNotification } from './generated/BackgroundTasksNotification';
export type { SubagentUsageDto } from './generated/SubagentUsageDto';

/** Context compaction (`chat_compacted`, header-only: uuid/trigger/preTokens) and CLI process
 *  exit (`cli_exited`). Generated from C# by TypeGen — re-exported here. */
export type { CompactedNotification } from './generated/CompactedNotification';
export type { EvictMessagesNotification } from './generated/EvictMessagesNotification';
export type { StatusNotification } from './generated/StatusNotification';
export type { CliExitedNotification } from './generated/CliExitedNotification';

/** Lazily-fetched compaction summary (`get_compact_summary` / `compact_summary_result`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { GetCompactSummaryRequest } from './generated/GetCompactSummaryRequest';
export type { GetCompactSummaryResponse } from './generated/GetCompactSummaryResponse';

/** A tool call's result (`chat_tool_result`).
 *  Generated from C# (Contracts.ToolResultNotification) by TypeGen — re-exported here. */
export type { ToolResultNotification } from './generated/ToolResultNotification';
/** Per-tool fields on a tool_result, grouped so adding another one touches the DTO and its
 *  renderer instead of every layer in between. Each member is null when its tool didn't report. */
export type { ToolResultExtrasDto } from './generated/ToolResultExtrasDto';
export type { AgentRunTotalsDto } from './generated/AgentRunTotalsDto';

/** Rate-limit notice (`chat_rate_limit`) + its severity union.
 *  Generated from C# by TypeGen — re-exported here. */
export type { RateLimitNotification } from './generated/RateLimitNotification';
export type { NoticeNotification } from './generated/NoticeNotification';
export type { NoticeVariantDto } from './generated/NoticeVariantDto';
export type { NoticePositionDto } from './generated/NoticePositionDto';

/** Remote Control connection status (`chat_remote_control`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { RemoteControlNotification } from './generated/RemoteControlNotification';

/** Active model / permission mode changed (`cli_model_changed` / `cli_permission_mode_changed`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { ModelChangedNotification } from './generated/ModelChangedNotification';
export type { PermissionModeChangedNotification } from './generated/PermissionModeChangedNotification';

/** A CLI-level error bubble (`cli_error`). Generated from C# by TypeGen. */
export type { CliErrorNotification } from './generated/CliErrorNotification';

/** A user message echo (`chat_user_text`) + its stripped image/document placeholders.
 *  Generated from C# by TypeGen — re-exported here. */
export type { UserTextNotification };
export type { UserImageDto } from './generated/UserImageDto';
export type { UserFileDto } from './generated/UserFileDto';

/** A tool_use surfaced for the tool row / permission banner (`chat_tool_permission`).
 *  `input` and `permissionSuggestions` are opaque (tool args / CLI PermissionUpdate).
 *  Generated from C# by TypeGen — re-exported here. */
export type { ToolPermissionNotification } from './generated/ToolPermissionNotification';
/** The CLI cancelled a pending permission (chat_tool_permission_cancel) — dismiss its banner.
 *  Generated from C# by TypeGen — re-exported here. */
export type { ToolPermissionCancelNotification } from './generated/ToolPermissionCancelNotification';

/** History page as replayed typed events / sub-agent transcript (chat_history /
 *  subagent_loaded). `HistoryEventDto.data` is opaque (a DTO), cast by `type`.
 *  Generated from C# by TypeGen — re-exported here. */
export type { HistoryEventDto } from './generated/HistoryEventDto';
export type { GetHistoryResponse } from './generated/GetHistoryResponse';
export type { HistoryLoadedNotification } from './generated/HistoryLoadedNotification';
export type { GetSubagentResponse } from './generated/GetSubagentResponse';

/** Prefill the composer (`ui_set_composer`). Generated from C# by TypeGen. */
export type { SetComposerNotification } from './generated/SetComposerNotification';

/** A key the host claimed for us (`ui_host_key`). Generated from C# by TypeGen. */
export type { HostKeyNotification } from './generated/HostKeyNotification';

/** Files the host claimed and read for us (`ui_files_dropped`). Generated from C# by TypeGen. */
export type { FilesDroppedNotification } from './generated/FilesDroppedNotification';

/** Prompt history, slash-command catalogue, streamed text delta, tool-progress tick,
 *  CLI-started notice. Generated from C# by TypeGen — re-exported here. */
export type { PromptHistoryNotification } from './generated/PromptHistoryNotification';
export type { SlashCommandsNotification } from './generated/SlashCommandsNotification';
export type { AssistantTextDeltaNotification } from './generated/AssistantTextDeltaNotification';
export type { ThinkingDeltaNotification } from './generated/ThinkingDeltaNotification';
export type { ThinkingEndedNotification } from './generated/ThinkingEndedNotification';
export type { ToolProgressNotification } from './generated/ToolProgressNotification';

/** Theme flip, @-mention suggestions wrapper, model-catalogue wrapper.
 *  Generated from C# by TypeGen — re-exported here. */
export type { ThemeChangedNotification } from './generated/ThemeChangedNotification';
export type { GetSuggestionsResponse } from './generated/GetSuggestionsResponse';
export type { ModelsNotification } from './generated/ModelsNotification';

/** A full assistant-text block (`chat_assistant_text`) with first-block usage.
 *  Generated from C# by TypeGen — re-exported here. */
export type { AssistantTextNotification } from './generated/AssistantTextNotification';

/** Turn finished (`chat_exchange_ended`): cost/duration + context-window info.
 *  Generated from C# by TypeGen — re-exported here. */
export type { ExchangeEndedNotification } from './generated/ExchangeEndedNotification';

// Init payload from the host — generated from C# (Contracts.InitPayloadNotification and its
// nested InitConfigDto/VsOptionsDto) by TypeGen. Same names as the C# side, so a DTO stays
// greppable across both languages; do not hand-edit the generated files.
export type { VsOptionsDto } from './generated/VsOptionsDto';
export type { InitPayloadNotification } from './generated/InitPayloadNotification';
// The CLI's own startup state, on its own message: it arrives seconds after the init payload,
// once claude.exe has answered initialize + get_settings.
export type { CliStateNotification } from './generated/CliStateNotification';

/** The five modes the picker offers. The CLI reports others too (`dontAsk`, and whatever a future
 *  version adds): they travel as their raw string rather than being coerced into one of these —
 *  a mode shown wrong is worse than a mode shown ugly. `string & {}` keeps the completions. */
export type PermissionMode =
    'default' | 'acceptEdits' | 'plan' | 'auto' | 'bypassPermissions' | (string & {});

/** The mode names as values. `string & {}` in the type above means a typo still compiles, so a
 *  comparison written by hand is unchecked — these give the compiler something to check. The
 *  strings are the CLI's, not ours: renaming one here renames nothing on the wire. */
export const PERMISSION_MODE = {
    default: 'default',
    acceptEdits: 'acceptEdits',
    plan: 'plan',
    auto: 'auto',
    bypassPermissions: 'bypassPermissions',
} as const satisfies Record<string, PermissionMode>;

// FromWebView input payloads (WebView → C#) — generated from C# (Contracts.*InputDto) by
// TypeGen. Used to type the bridge.post(...) call sites so a payload that diverges from the
// C# DTO fails at compile time. Opposite direction of the ToWebView DTOs above.
export type { SendPromptNotification } from './generated/SendPromptNotification';
export type { RespondPermissionNotification } from './generated/RespondPermissionNotification';
export type { SetSendSelectionNotification } from './generated/SetSendSelectionNotification';
export type { IdeFileNotification } from './generated/IdeFileNotification';
export type { GetSuggestionsRequest } from './generated/GetSuggestionsRequest';
export type { RewindRequest } from './generated/RewindRequest';
export type { RewindResultNotification } from './generated/RewindResultNotification';
export type { RewindDiffNotification } from './generated/RewindDiffNotification';
export type { RewindPointsNotification } from './generated/RewindPointsNotification';

/** One user message as the rewind dialog needs it: what to show, and what to rewind to. Not a wire
 *  type — it is built from the transcript, which is why it lives here and not in generated/. */
export interface RewindPoint {
    uuid: string;
    text: string;
    timestamp?: number;
}
export type { ToolOutputNotification } from './generated/ToolOutputNotification';
export type { GetSubagentRequest } from './generated/GetSubagentRequest';
export type { SubagentCancelNotification } from './generated/SubagentCancelNotification';
export type { SubagentDetachNotification } from './generated/SubagentDetachNotification';
export type { GetHistoryRequest } from './generated/GetHistoryRequest';
export type { GetUsageRequest } from './generated/GetUsageRequest';
export type { UsageDto } from './generated/UsageDto';
export type { RateWindowDto } from './generated/RateWindowDto';
export type { UsageBehaviorsDto } from './generated/UsageBehaviorsDto';
export type { UsageInsightDto } from './generated/UsageInsightDto';
export type { UsageAttributionDto } from './generated/UsageAttributionDto';
export type { GetContextUsageRequest } from './generated/GetContextUsageRequest';
export type { GetContextUsageResponse } from './generated/GetContextUsageResponse';
export type { GetStatsRequest } from './generated/GetStatsRequest';
export type { StatsResponse } from './generated/StatsResponse';
export type { StatsScopeDto } from './generated/StatsScopeDto';
export type { StatsRangeDto } from './generated/StatsRangeDto';
export type { StatsModelDto } from './generated/StatsModelDto';
export type { StatsDayDto } from './generated/StatsDayDto';
export type { StatsDayModelDto } from './generated/StatsDayModelDto';
export type { StatsToolDto } from './generated/StatsToolDto';
export type { ContextCategoryDto } from './generated/ContextCategoryDto';
export type { ContextGridCellDto } from './generated/ContextGridCellDto';
export type { ContextTokenGroupDto } from './generated/ContextTokenGroupDto';
export type { OpenDocumentNotification } from './generated/OpenDocumentNotification';
export type { OpenAttachmentNotification } from './generated/OpenAttachmentNotification';
export type { DiffDialogNotification } from './generated/DiffDialogNotification';
export type { SetPermissionModeNotification } from './generated/SetPermissionModeNotification';
export type { SetModelNotification } from './generated/SetModelNotification';
export type { ForkNotification } from './generated/ForkNotification';
export type { ExternalUrlNotification } from './generated/ExternalUrlNotification';
export type { OpenOptionsNotification } from './generated/OpenOptionsNotification';

/** Global signal that a permission prompt is active (cv-prompt reads its
 *  presence to disable sending). The banner itself holds the full request
 *  details locally — only id/name are needed here. */
export interface PendingPermission {
    id: string;
    name: string;
}

/** Editor selection / active file (`ide_selection_changed`), drives the composer
 *  IDE-context badge. Generated from C# by TypeGen — re-exported here. */
export type { IdeContextNotification } from './generated/IdeContextNotification';

// Bare reference to a file open/selected in the IDE; the UI layer turns each one into a chip.
// Two sources: the composer on submit, and parseIdeContextTags on a replayed message.
export interface IdeContextRef {
    filePath: string;
    startLine?: number;
    endLine?: number;
}

/** The host's notification plus the editor refs the composer holds itself. A field rather than an
 *  `<ide_*>` tag on the text: the tag the model reads is composed host-side, and one glued on here
 *  would only be parsed back apart by this same WebView. */
export interface UserTextEcho extends UserTextNotification {
    ideRefs?: IdeContextRef[];
}

/** Token usage of a single assistant turn — generated from C# (Contracts.ContextUsageDto)
 *  by TypeGen. Re-exported here so it sits with the other shared types; do not hand-edit
 *  the generated file. Context consumed = input + cache_read + cache_creation. */
export type { ContextUsageDto } from './generated/ContextUsageDto';

/** Spinner-verb override from settings (nested in the init ui payload). Generated from
 *  C# (Contracts.SpinnerVerbsConfigDto); mode is a raw string ("append"/"replace"). */
export type { SpinnerVerbsConfigDto } from './generated/SpinnerVerbsConfigDto';

/** A file picked/dropped for upload. Always read as base64 (one code path);
 *  the host decides by extension whether to send it as an image, a PDF, or
 *  decode the base64 back to text. `isImage`/`dataUrl` drive the chip preview. */
export interface Attachment {
    name: string;
    mediaType: string;
    isImage: boolean;
    base64: string;
    dataUrl: string;
    /** Tiny PNG data-URI thumbnail for the sent-message chip (images only). Kept small so it
     *  can ride in the echoed message instead of the full dataUrl. Undefined for non-images. */
    preview?: string;
}

/** An active sub-agent (Agent/Skill/Task) tracked while it runs. Mirrors the CLI's
 *  task_* events; removed on task_notification / result. */
export interface SubagentTask {
    taskId: string;
    description: string;
    toolUseId?: string;
    recentTools: string[]; // last 3 tool names — at(-1) is what it is doing now
    summary?: string;
    usage: SubagentUsageDto;
    /** Running in the background, so it outlives the turn that launched it. Set from the
     *  authoritative id list (background_tasks_changed), not from task_updated's is_backgrounded:
     *  that flag only ever describes a foreground task being pushed down, and the agents the CLI
     *  launches asynchronously are background from birth — measured, they never emit it. */
    background?: boolean;
    /** Last status the CLI reported for it — 'paused' and 'killed' have no other way in. Undefined
     *  until a patch carries one. */
    status?: string;
    /** The task that launched this one, undefined at top level. The wire carries no parent link,
     *  so it is derived from where the launching row sits in the entry tree; it settles once that
     *  row has arrived (the task can beat it by a few ms). */
    parentTaskId?: string;
    /** When we saw the task start (epoch ms). `usage.durationMs` only advances when the sub-agent
     *  reports a tool use — it can sit still for ten seconds on a long call — so the running badge
     *  counts from here instead, and falls back to the reported figure once the task ends. */
    startedAt?: number;
}

/** Status of a tool call: pending (spinner) | done (green) | error (red). */
export type ToolStatus = 'pending' | 'done' | 'error';

/** A tool_use block. Input shape varies per tool; renderers pluck the fields they need. */
export interface ToolUseData {
    id: string;
    name: string;
    input?: Record<string, unknown>;
}

export type MessageRole =
    'user' | 'assistant' | 'compact' | 'status' | 'error' | 'result' | 'slash-result';

/** Shared by image/file chips: a name and optional lazy-fetch coords (the host
 *  strips heavy blocks from history; the chip fetches them on demand). */
interface UiAttachment {
    name: string;
    lazy?: { uuid: string; blockIdx: number };
}

/** Inline image shown in a message. `preview` is a tiny PNG data-URI for the chip
 *  thumbnail (from the live paste or the host thumbnail); the full image is fetched
 *  lazily on click. `dataUrl`, when present, is a live full image. */
export interface UiImage extends UiAttachment {
    dataUrl?: string;
    preview?: string;
}

/** File attachment shown as a chip. Same shape as UiAttachment (name + lazy) — the
 *  named alias keeps `files: UiFile[]` distinct from `images` at call sites. Click
 *  fetches the stripped document (lazy); attachments carry no file-path/line. */
export type UiFile = UiAttachment;

/** Shared base for every text entry (a message bubble). `role` discriminates the members. */
interface UiEntryBase {
    kind: 'text';
    id: number;
    text: string;
    /** Message time (epoch ms) from the record/event; absent when the wire had none. Drives the
     *  actions row's "x ago". Not every role shows it (thinking/compact/status don't). */
    timestamp?: number;
}

/** A real user turn: the prompt text plus optional image/file chips. */
export interface UiUserEntry extends UiEntryBase {
    role: 'user';
    uuid?: string;
    images?: UiImage[];
    files?: UiFile[];
    /** What the editor was showing when this turn was sent. Taken out of `text` at build time,
     *  so a long selection doesn't sit in the transcript for the whole session. */
    ideRefs?: IdeContextRef[];
}

/** An assistant turn; `streaming` is UI-only (true while the delta text is still growing). */
export interface UiAssistantEntry extends UiEntryBase {
    role: 'assistant';
    streaming?: boolean;
    /** The message's wire uuid — what lets an entry be addressed after it has been rendered.
     *  Absent on a synthetic message (the permission banner's) and on older .jsonl lines. */
    uuid?: string;
    /** Why the API call failed, when it did (`overloaded`, `rate_limit`, …). An API failure reaches
     *  us as an assistant message whose text IS the error, so this is what tells the two apart —
     *  without it the turn renders as an answer, grey dot and all. Absent on a normal message, and
     *  on history: the .jsonl keeps no such field. */
    error?: string;
}

/** A thinking block: the model's reasoning, live-only (never persisted). `streaming` true while
 *  deltas grow; `tokens` is the live estimate (delta-accumulated or, once seen, the authoritative
 *  thinking_tokens value). `redacted` = cipher-only block, no text → static, not expandable. */
export interface UiThinkingEntry extends UiEntryBase {
    role: 'thinking';
    streaming?: boolean;
    tokens?: number;
    durationMs?: number;
    redacted?: boolean;
    /** Set once an authoritative thinking_tokens value arrives; delta estimates stop accumulating. */
    tokensAuthoritative?: boolean;
    /** First-delta timestamp (ms) to compute durationMs on end. UI-only. */
    startedAt?: number;
}

/** A compaction boundary: header (trigger/preTokens) from the notification, plus the summary
 *  fetched lazily on first expand (cached via `loaded`). All UI-only beyond the header. */
export interface UiCompactEntry extends UiEntryBase {
    role: 'compact';
    uuid: string;
    trigger: string;
    preTokens: number;
    summary?: string;
    loaded?: boolean;
}

/** A slash command's local output (<local-command-stdout>/<stderr>), parsed TS-side from a user
 *  message — a ViewModel-only role (no DTO/SDK peer). Rendered as a preformatted monospace block. */
export interface UiSlashResultEntry extends UiEntryBase {
    role: 'slash-result';
    isError: boolean;
}

/** Plain single-line notices with no extra state (CLI error, turn result, model-switch status). */
export interface UiSimpleTextEntry extends UiEntryBase {
    role: 'error' | 'result' | 'status';
}

/** The Remote Control session link and its QR code, posted into the transcript when the bridge
 *  comes up — like the CLI, which prints the URL in the conversation. A role of its own because
 *  the QR is an inline SVG, which the markdown pipeline strips. `text` carries the URL. */
export interface UiRemoteControlEntry extends UiEntryBase {
    role: 'remote-control';
}

/** One AskUserQuestion entry, narrowed from the opaque tool input (the CLI's
 *  AskUserQuestion tool has no generated DTO — it rides inside
 *  ToolPermissionNotification.input). The permission banner writes/reads it and the
 *  tool-renderer shows it read-only, both defensively. */
export interface AskQuestion {
    question: string;
    header?: string;
    multiSelect?: boolean;
    options: { label: string; description?: string }[];
}

/** A tool row's nested children (the Agent tool's transcript today; the mechanism is generic).
 *  Memoria-minima: at most the 3 most-recent items are kept; expand fetches more lazily. */
export interface ToolChildren {
    /** The kept child rows/messages (≤3 collapsed, the full list once "Show all" fetched). */
    items: UiEntry[];
    /** True once a 4th child arrived: more exist beyond the kept items. A flag, not a count —
     *  the "…" only signals "more", never the number. */
    hasMore: boolean;
    /** Show-all: `items` holds the full transcript and the view renders all of it, vs the
     *  last-3 ring. Collapse ("Reduce") clears it. Distinct from the row's open/closed state. */
    showAll: boolean;
}

export interface UiToolEntry {
    kind: 'tool';
    id: number;
    toolUseId: string;
    /** agentId identifies this as a sub-agent invocation (Agent tool). */
    agentId?: string;
    data: ToolUseData;
    status: ToolStatus;
    result: string;
    /** Non-empty line count of the FULL output (before preview truncation), 0 when empty.
     *  Count-only renderers (Grep/Glob) show this; the full text is re-read on click. */
    fullLineCount: number;
    elapsedSec: number;
    /** Per-tool fields from the result: the edit's line range, what an Agent run cost. Absent until
     *  the tool finishes, and for a tool that reports neither — which is most of them. */
    extras?: ToolResultExtrasDto | null;
    /** Nested children (Agent tool today; any tool with children). Present only when the tool
     *  has children — undefined for a normal leaf tool. NOT the row open/closed state: that's
     *  the component's local `_expanded`, which every tool has whether or not it has children. */
    children?: ToolChildren;
}

/** A rendered chat entry: a message bubble (one per role) or a tool row (with optional nested
 *  children). Discriminated on `kind` (text/tool) then, for text, on `role`. */
export type UiEntry =
    | UiUserEntry
    | UiAssistantEntry
    | UiThinkingEntry
    | UiCompactEntry
    | UiSlashResultEntry
    | UiSimpleTextEntry
    | UiRemoteControlEntry
    | UiToolEntry;

/** Payload for the full-screen image viewer (cv-lightbox), passed via openLightbox(). */
export interface LightboxRequest {
    src: string;
    name?: string;
}

/** One dismissible notice in the stack above the composer (rate limits, CLI `informational`
 *  warnings, upload errors…). `key` dedups repeats of the same condition (a second arrival with the
 *  same key replaces rather than stacks); `id` identifies the row for dismissal. info/success
 *  auto-dismiss, warning/error stay until dismissed. */
export interface Notice {
    id: string;
    severity: 'info' | 'success' | 'warning' | 'error';
    message: string;
    key?: string;
    /** Optional action button: label + the fromWebView bridge message its click sends. */
    actionLabel?: string;
    actionMessage?: string;
    /** Payload for that message, when it takes one (which Options page to open). */
    actionPayload?: Record<string, unknown>;
    /** Stays until the host clears it (a dead CLI process) — never auto-dismissed. */
    sticky?: boolean;
    /** Hide the ✕. For a notice that mirrors live state rather than reporting an event: dismissing
     *  it would leave the state on with nothing on screen saying so. Implies `sticky`. */
    pinned?: boolean;
    /** Raw SVG replacing the severity icon, for a row whose subject is recognisable at a glance
     *  (a phone for Remote Control). The severity still sets the colour. */
    icon?: string;
}

/** Payload of `notice-dismissed`, raised only when the user clicks a notice's ✕. */
export interface NoticeDismissedDetail {
    key?: string;
}

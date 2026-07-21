/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Shared TypeScript interfaces. Intentionally light: we type only the
// pieces we touch, not every JSON shape the C# host can send.

import type { SubagentUsageDto } from './generated/SubagentUsageDto';

export type Theme = 'dark' | 'light';

/** Reasoning effort level union ('low'|'medium'|'high'|'xhigh'), generated from C#. */
export type { EffortLevelDto } from './generated/EffortLevelDto';

/** Slider levels (low→max) and their labels, matching the VS Code extension. */
export const EFFORT_SLIDER_LEVELS = ['low', 'medium', 'high', 'xhigh', 'max'] as const;
export type EffortSliderLevel = (typeof EFFORT_SLIDER_LEVELS)[number];
export const EFFORT_LEVEL_LABELS: Readonly<Record<EffortSliderLevel, string>> = {
    low: 'Low',
    medium: 'Medium',
    high: 'High',
    xhigh: 'Extra high',
    max: 'Max',
};

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
export type { SubagentUsageDto } from './generated/SubagentUsageDto';

/** Context compaction (`chat_compacted`, header-only: uuid/trigger/preTokens) and CLI process
 *  exit (`cli_exited`). Generated from C# by TypeGen — re-exported here. */
export type { CompactedNotification } from './generated/CompactedNotification';
export type { StatusNotification } from './generated/StatusNotification';
export type { CliExitedNotification } from './generated/CliExitedNotification';

/** Lazily-fetched compaction summary (`get_compact_summary` / `compact_summary_result`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { GetCompactSummaryRequest } from './generated/GetCompactSummaryRequest';
export type { GetCompactSummaryResponse } from './generated/GetCompactSummaryResponse';

/** A tool call's result (`chat_tool_result`).
 *  Generated from C# (Contracts.ToolResultNotification) by TypeGen — re-exported here. */
export type { ToolResultNotification } from './generated/ToolResultNotification';

/** Rate-limit notice (`chat_rate_limit`) + its severity union.
 *  Generated from C# by TypeGen — re-exported here. */
export type { RateLimitNotification } from './generated/RateLimitNotification';
export type { NoticeVariantDto } from './generated/NoticeVariantDto';

/** Active model / permission mode changed (`cli_model_changed` / `cli_permission_mode_changed`).
 *  Generated from C# by TypeGen — re-exported here. */
export type { ModelChangedNotification } from './generated/ModelChangedNotification';
export type { PermissionModeChangedNotification } from './generated/PermissionModeChangedNotification';

/** A CLI-level error bubble (`cli_error`). Generated from C# by TypeGen. */
export type { CliErrorNotification } from './generated/CliErrorNotification';

/** A user message echo (`chat_user_text`) + its stripped image/document placeholders.
 *  Generated from C# by TypeGen — re-exported here. */
export type { UserTextNotification } from './generated/UserTextNotification';
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
// nested InitConfigDto/VsOptionsDto) by TypeGen. Aliased to the historical names the
// UI consumes; do not hand-edit the generated files.
export type { InitConfigDto as InitConfig } from './generated/InitConfigDto';
export type { VsOptionsDto as VsOptionsConfig } from './generated/VsOptionsDto';
export type { InitPayloadNotification as InitPayload } from './generated/InitPayloadNotification';

export type PermissionMode = 'default' | 'acceptEdits' | 'plan' | 'auto' | 'bypassPermissions';

// FromWebView input payloads (WebView → C#) — generated from C# (Contracts.*InputDto) by
// TypeGen. Used to type the bridge.post(...) call sites so a payload that diverges from the
// C# DTO fails at compile time. Opposite direction of the ToWebView DTOs above.
export type { SendPromptNotification } from './generated/SendPromptNotification';
export type { RespondPermissionNotification } from './generated/RespondPermissionNotification';
export type { SetSendSelectionNotification } from './generated/SetSendSelectionNotification';
export type { IdeFileNotification } from './generated/IdeFileNotification';
export type { IdeFileAtEditNotification } from './generated/IdeFileAtEditNotification';
export type { GetSuggestionsRequest } from './generated/GetSuggestionsRequest';
export type { ToolOutputNotification } from './generated/ToolOutputNotification';
export type { GetSubagentRequest } from './generated/GetSubagentRequest';
export type { SubagentCancelNotification } from './generated/SubagentCancelNotification';
export type { GetHistoryRequest } from './generated/GetHistoryRequest';
export type { GetUsageRequest } from './generated/GetUsageRequest';
export type { GetUsageResponse } from './generated/GetUsageResponse';
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

// Bare reference to a file open/selected in the IDE.
// Returned by parseIdeContextTags; the UI layer turns each ref into a chip.
export interface IdeContextRef {
    filePath: string;
    startLine?: number;
    endLine?: number;
}

/** Token usage of a single assistant turn — generated from C# (Contracts.ContextUsageDto)
 *  by TypeGen. Re-exported here so it sits with the other shared types; do not hand-edit
 *  the generated file. Context consumed = input + cache_read + cache_creation. */
export type { ContextUsageDto } from './generated/ContextUsageDto';

/** Spinner-verb override from settings (nested in the init ui payload). Generated from
 *  C# (Contracts.SpinnerVerbsConfigDto); mode is a raw string ("append"/"replace"). */
export type { SpinnerVerbsConfigDto as SpinnerVerbsConfig } from './generated/SpinnerVerbsConfigDto';

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
    taskType?: string;
    toolUseId?: string;
    recentTools: string[]; // last 3 tool names (cosa fa ora = at(-1))
    summary?: string;
    usage: SubagentUsageDto;
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
}

/** A real user turn: the prompt text plus optional image/file chips. */
export interface UiUserEntry extends UiEntryBase {
    role: 'user';
    uuid?: string;
    images?: UiImage[];
    files?: UiFile[];
}

/** An assistant turn; `streaming` is UI-only (true while the delta text is still growing). */
export interface UiAssistantEntry extends UiEntryBase {
    role: 'assistant';
    streaming?: boolean;
}

/** A thinking block: the model's reasoning, live-only (never persisted). `streaming` true while
 *  deltas grow; `tokens` is the live estimate (delta-accumulated or, once seen, the authoritative
 *  thinking_tokens value). `redacted` = cipher-only block, no text → static, not expandable. */
export interface UiThinkingEntry extends UiEntryBase {
    role: 'thinking';
    parentToolUseId?: string;
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
 *  message — a ViewModel-only role (no DTO/SDK peer). Rendered as a centered berry pill. */
export interface UiSlashResultEntry extends UiEntryBase {
    role: 'slash-result';
    isError: boolean;
}

/** Plain single-line notices with no extra state (CLI error, turn result, model-switch status). */
export interface UiSimpleTextEntry extends UiEntryBase {
    role: 'error' | 'result' | 'status';
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
    /** Sub-agent children. Memoria-minima: at most the 3 most-recent are kept;
     *  expand re-fetches the full list (lazy), collapse shows the last 3 again. */
    subagentChildren?: UiEntry[];
    /** True once a 4th child arrived: more exist beyond the 3 kept in subagentChildren.
     *  A flag, not a count — the "…" only signals "more", never the number. */
    hasMore?: boolean;
    /** Nested box expanded: subagentChildren holds the full list (not the ring of 3) and
     *  new children are upserted in full rather than sliced. Collapse clears it. */
    expanded?: boolean;
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
    | UiToolEntry;

/** Payload for the full-screen image viewer (cv-lightbox), passed via openLightbox(). */
export interface LightboxRequest {
    src: string;
    name?: string;
}

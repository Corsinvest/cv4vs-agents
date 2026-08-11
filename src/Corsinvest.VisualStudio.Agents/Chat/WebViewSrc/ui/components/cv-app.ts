/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import ChevronDown16Regular from '@fluentui/svg-icons/icons/chevron_down_16_regular.svg';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { fetchSubagent, fetchContextUsage, fetchCompactSummary } from '../../core/lazy';
import { Transcript } from '../../core/transcript';
import { clearMarkdownCache } from '../../core/markdown';
import { buildGroups } from '../../core/exchanges';
import './cv-notice-stack';
import type { CvNoticeStack } from './cv-notice-stack';
import './cv-welcome';
import './cv-prompt';
import './cv-message';
import './cv-copy-btn';
import { renderActionsRow, type TurnMetrics } from '../helpers/actions-row';
import './cv-thinking';
// Dialogs are created on demand by core/dialog-host (which must not import ui/),
// so register their custom elements here in the UI layer.
import './cv-usage-dialog';
import './cv-stats-dialog';
import './cv-context-dialog';
import './cv-plugin-manager';
import './cv-lightbox';
import './cv-diff-dialog';
import './cv-permission-banner';
import './cv-spinner';
import './cv-tool-row';
import { state as appState } from '../../core/state';
import type {
    SubagentTask,
    UiEntry,
    UiToolEntry,
    UiImage,
    UiFile,
    UiUserEntry,
    UiAssistantEntry,
    UiThinkingEntry,
    UiCompactEntry,
    UiSlashResultEntry,
    SubagentStartedNotification,
    SubagentProgressNotification,
    SubagentEndedNotification,
    CompactedNotification,
    EvictMessagesNotification,
    StatusNotification,
    ToolResultNotification,
    ModelChangedNotification,
    AssistantTextDeltaNotification,
    ThinkingDeltaNotification,
    ThinkingEndedNotification,
    ToolProgressNotification,
    AssistantTextNotification,
    ExchangeEndedNotification,
    CliErrorNotification,
    UserTextNotification,
    ToolPermissionNotification,
    HistoryEventDto,
    HistoryLoadedNotification,
    NoticeNotification,
    CliExitedNotification,
    RemoteControlNotification,
} from '../../core/types';
import { GetHistoryReq } from '../../core/request-types';
import { modelLabel } from '../../core/ai-models';
import { turnErrorLabel, turnErrorDetail, isUserAbort } from '../../core/turn-errors';
import { parseLocalCommandOutput } from '../../core/slash-commands';
import { escapeHtml } from '../../core/html';

let _entryIdSeq = 0;

/** Notice key for the "CLI process exited" row, so a restart clears exactly that one. */
const CLI_EXITED_KEY = 'cli-exited';
/** Notice keys for the Remote Control state/error banners — see _onRemoteControl. */
const REMOTE_CONTROL_KEY = 'remote-control';
const REMOTE_CONTROL_ERROR_KEY = 'remote-control-error';

/**
 * Root component. Owns the chat entry list and wires the bridge messages
 * that produce or update entries (`user_text`, `text`, `text_delta`,
 * `tool_use`, `tool_result`, `tool_progress`, `result`, `error`,
 * `clear`, `history`).
 */
@customElement('cv-app')
export class CvApp extends LitElement {
    /** Owns the transcript. Deliberately not reactive — _mutate() is the one place that tells
     *  Lit something changed, so there is no second path that can forget to. */
    private _transcript = new Transcript();
    /** The only thing Lit watches for a transcript change. Lit re-renders on a reactive property
     *  it sees change by identity, and the tree lives in a plain object it cannot see into — so
     *  _mutate() bumps this instead. A counter and not a flag: Lit compares the value from
     *  before the batch with the one after, so two invalidations in the same microtask would
     *  toggle a boolean back to where it started and the render would be skipped. */
    @state() private _rev = 0;
    /** Groups for the current tree, keyed by the array they were built from. */
    private _groupCache: { src: readonly UiEntry[]; groups: UiEntry[][] } | null = null;
    @state() private _subagentTasks = new Map<string, SubagentTask>();
    @state() private _isBusy = appState.isBusy;
    @state() private _status = appState.status;
    /** A permission/Ask prompt is awaiting the user → hide the "waiting" spinner
     *  (Claude isn't working, it's waiting for the user to choose). */
    @state() private _awaitingUser = appState.pendingPermission != null;

    @query('#messages') private _messagesEl!: HTMLDivElement;
    /** Whether the "jump to the latest" button is showing — see _updateJump for the hysteresis. */
    @state() private _showJump = false;
    private _jumpRaf = 0;
    @query('#system-notices') private _systemNotices!: CvNoticeStack | null;

    private _offs: Array<() => void> = [];
    /**
     * Currently-streaming assistant message, keyed by parent tool_use_id
     * (`''` for root). Lets a sub-agent stream concurrently with the root
     * without mixing the two; delta lookups default to `''`.
     */
    private _streamingMsgs = new Map<string, number>();
    /** Currently-streaming thinking block, keyed by parentToolUseId (same nesting rule
     *  as _streamingMsgs). Never persisted — cleared on session clear. */
    private _thinkingMsgs = new Map<string, number>();
    /** What a finished turn cost, keyed by the id of its last text block — the entry the exchange's
     *  actions row renders against. Two writers: `assistantText` files the token counts (which the
     *  JSONL keeps, so they survive a replay), `exchangeEnded` adds cost and duration (which ride
     *  `result` and are live-only). A replayed turn therefore shows its tokens and nothing else. */
    private _turnMetrics = new Map<number, TurnMetrics>();

    /** Uuids of bubbles echoed on submit but not yet handed to the CLI, mirrored from cv-prompt's
     *  queue (which keeps the payloads). A Set because renderMessage asks per bubble. */
    @state() private _queuedUuids = new Set(appState.queuedUuids);

    /** Derived, never stored: recomputed only when the tree changes identity. Holding the groups
     *  as state gave them two writers, and that is how they and the entries drifted apart. */
    private get _exchanges(): UiEntry[][] {
        const src = this._transcript.entries;
        if (this._groupCache?.src !== src) {
            this._groupCache = { src, groups: buildGroups(src) };
        }
        return this._groupCache.groups;
    }

    /** Every transcript change goes through here, so one place tells Lit about all of them. */
    private _mutate(fn: () => void): void {
        fn();
        this._rev++;
    }

    /**
     * Forget ids the transcript has dropped — wired to its onRemoved, so a removal cannot land
     * without this running.
     *
     * Three maps here are keyed on entry ids, and none of them belongs in Transcript: they track
     * what is still streaming and what a turn cost, none of which is about the shape of the tree.
     * What they do share is that an id leaving the tree strands them — deltas written into an entry
     * that is gone, or a Map that never shrinks. The clears at session switch and history push
     * cover the wholesale case; this is the piecemeal one.
     */
    private _dropRemovedIds(ids: readonly number[]): void {
        const gone = new Set(ids);
        for (const [parentId, id] of [...this._streamingMsgs]) {
            if (gone.has(id)) {
                this._streamingMsgs.delete(parentId);
            }
        }
        for (const [parentId, id] of [...this._thinkingMsgs]) {
            if (gone.has(id)) {
                this._thinkingMsgs.delete(parentId);
            }
        }
        for (const id of gone) {
            this._turnMetrics.delete(id);
        }
    }

    /** Same three maps, for when the whole tree goes (transcript's onCleared). */
    private _dropAllIds(): void {
        this._streamingMsgs.clear();
        this._thinkingMsgs.clear();
        this._turnMetrics.clear();
    }

    override createRenderRoot() {
        return this;
    }

    override connectedCallback(): void {
        super.connectedCallback();
        // Wired here rather than at the field: the initializer runs before `this` is usable.
        this._transcript.onRemoved = (ids) => this._dropRemovedIds(ids);
        this._transcript.onCleared = () => this._dropAllIds();
        this._offs.push(appState.on('isBusy', (v) => (this._isBusy = v)));
        this._offs.push(appState.on('queuedUuids', (v) => (this._queuedUuids = new Set(v))));
        this._offs.push(appState.on('status', (v) => (this._status = v)));
        this._offs.push(appState.on('pendingPermission', (v) => (this._awaitingUser = v != null)));
        // Global Esc-to-stop: interrupt generation regardless of focus, so stopping
        // never depends on where the caret is. Skipped when a permission/Ask prompt
        // is open — there Esc cancels the prompt (the banner handles it, with stopPropagation).
        window.addEventListener('keydown', this._onGlobalEsc);
        // A nested Agent box toggled. Expand: fetch the full transcript (subagent_loaded
        // upserts it + sets expanded). Collapse: drop back to the last 3 here.
        this.addEventListener('subagent-toggle', this._onChildrenToggle as EventListener);
        // A compact separator's <details> opened for the first time: fetch the summary
        // (lazy, cached via the entry's `loaded` flag — collapse/re-expand doesn't refetch).
        this.addEventListener('compact-expand', this._onCompactExpand as EventListener);
        // User picked a model from the menu (cv-prompt) — the "Switched to X" notice
        // fires ONLY here, never for the ui_init seed or a runtime cli_model_changed.
        this.addEventListener('model-switched', this._onModelSwitched as EventListener);
        // A queued message reached the CLI: its bubble moves down to where it was really sent (the
        // fading comes from appState.queuedUuids, which needs no event).
        this.addEventListener('queued-sent', this._onQueuedSent as EventListener);
        // Stop / Clear: what never went is taken off screen rather than left looking sent.
        this.addEventListener('queued-dropped', this._onQueuedDropped as EventListener);

        // Session/system notices: this stack keeps the `top` ones (a per-turn notice is picked up by
        // cv-prompt's own stack listening on the same channel).
        this._offs.push(
            bridge.onNotification<NoticeNotification>(Msg.toWebView.chat.notice, (d) => {
                if (!d?.message || (d.position ?? 'top') !== 'top') {
                    return;
                }
                this._systemNotices?.push({
                    severity: d.severity ?? 'info',
                    message: d.message,
                    key: d.key ?? undefined,
                    actionLabel: d.actionLabel ?? undefined,
                    actionMessage: d.actionMessage ?? undefined,
                    sticky: !!d.sticky,
                });
            }),
        );

        // The CLI process died: a sticky error with a "View logs" action, cleared when it restarts.
        // (An intentional exit is a respawn we triggered — session switch/resume/workdir — not a crash.)
        this._offs.push(
            bridge.onNotification<CliExitedNotification>(Msg.toWebView.cli.exited, (d) => {
                if (d?.intentional) {
                    this._systemNotices?.dismissByKey(CLI_EXITED_KEY);
                    return;
                }
                const code = d?.exitCode;
                this._systemNotices?.push({
                    severity: 'error',
                    message:
                        code != null && code !== 0
                            ? `Claude Code process exited (code ${code})`
                            : 'Claude Code process exited',
                    key: CLI_EXITED_KEY,
                    actionLabel: 'View logs',
                    actionMessage: Msg.fromWebView.open.ideOutputWindow,
                    sticky: true,
                });
            }),
        );
        this._offs.push(
            bridge.onNotification(Msg.toWebView.cli.started, () => {
                this._systemNotices?.dismissByKey(CLI_EXITED_KEY);
            }),
        );

        this._offs.push(
            bridge.onNotification<RemoteControlNotification>(
                Msg.toWebView.chat.remoteControl,
                (d) => this._onRemoteControl(d),
            ),
        );

        this._offs.push(
            bridge.onNotification<UserTextNotification>(Msg.toWebView.chat.userText, (data) => {
                const entry = CvApp.buildUserEntry(data);
                if (!entry) {
                    return;
                }
                // Live message: use its wire time, or now as a fallback (a live turn is "now").
                if (entry.role === 'user') {
                    entry.timestamp = data.timestamp ?? Date.now();
                }
                this._appendEntry(entry, data.parentToolUseId ?? undefined);
                queueMicrotask(() => this._scrollToBottom());
            }),
        );

        this._offs.push(
            bridge.onNotification<AssistantTextNotification>(
                Msg.toWebView.chat.assistantText,
                (data) => {
                    const parentId = data?.parentToolUseId ?? '';
                    // Gauge tracks main-thread usage only (sub-agents would skew it).
                    if (!parentId && data?.usage) {
                        appState.contextUsage = data.usage;
                    }
                    const streamingId = this._streamingMsgs.get(parentId);
                    let entryId: number | undefined;
                    if (streamingId !== undefined) {
                        this._mutate(() =>
                            this._transcript.update<UiAssistantEntry>(streamingId, (e) => ({
                                ...e,
                                text: data?.text ?? e.text,
                                streaming: false,
                                // The final assistant notification carries the message time;
                                // live fallback = now.
                                timestamp: data?.timestamp ?? Date.now(),
                                // Only this final notification names the message — the deltas that
                                // built the entry carry no uuid, so it lands here or nowhere.
                                uuid: data?.uuid ?? e.uuid,
                                // Same: an API failure is only known once the frame arrives.
                                error: data?.error ?? e.error,
                            })),
                        );
                        entryId = streamingId;
                        this._streamingMsgs.delete(parentId);
                    } else {
                        const entry = CvApp.buildAssistantEntry(data);
                        entry.timestamp = data?.timestamp ?? Date.now();
                        this._appendEntry(entry, parentId);
                        entryId = entry.id;
                        queueMicrotask(() => this._scrollToBottom());
                    }
                    this._seedTurnTokens(entryId, data);
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<AssistantTextDeltaNotification>(
                Msg.toWebView.chat.assistantTextDelta,
                (data) => {
                    const delta = data?.text ?? '';
                    const parentId = data?.parentToolUseId ?? '';
                    const streamingId = this._streamingMsgs.get(parentId);
                    if (streamingId === undefined) {
                        this._streamingMsgs.set(
                            parentId,
                            this._addText<UiAssistantEntry>(
                                { role: 'assistant', text: delta, streaming: true },
                                parentId,
                            ),
                        );
                    } else {
                        // Auto-follow only if already near the bottom.
                        const atBottom = this._isNearBottom();
                        this._mutate(() => this._transcript.appendText(streamingId, delta));
                        if (atBottom) {
                            queueMicrotask(() => this._scrollToBottomNow());
                        }
                    }
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ThinkingDeltaNotification>(
                Msg.toWebView.chat.thinkingDelta,
                (data) => {
                    const parentId = data?.parentToolUseId ?? '';
                    let entryId = this._thinkingMsgs.get(parentId);
                    if (entryId === undefined) {
                        entryId = this._addText<UiThinkingEntry>(
                            {
                                role: 'thinking',
                                text: '',
                                streaming: true,
                                tokens: 0,
                                startedAt: Date.now(),
                            },
                            parentId,
                        );
                        this._thinkingMsgs.set(parentId, entryId);
                    }
                    this._mutate(() =>
                        this._transcript.update<UiThinkingEntry>(entryId, (e) => {
                            const next = { ...e };
                            if (data?.text) {
                                next.text += data.text;
                            }
                            // Token: authoritative thinking_tokens (text empty + estimate>=0) SETS
                            // and locks; deltas accumulate until then.
                            if (data && data.estimatedTokens >= 0) {
                                if (!data.text) {
                                    next.tokens = data.estimatedTokens;
                                    next.tokensAuthoritative = true;
                                } else if (!next.tokensAuthoritative) {
                                    next.tokens = (next.tokens ?? 0) + data.estimatedTokens;
                                }
                            }
                            return next;
                        }),
                    );
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ThinkingEndedNotification>(
                Msg.toWebView.chat.thinkingEnded,
                (data) => {
                    const parentId = data?.parentToolUseId ?? '';
                    let entryId = this._thinkingMsgs.get(parentId);
                    // A redacted_thinking block has no preceding delta → no entry yet: create a static,
                    // text-less one here so the user still sees that the model thought.
                    if (entryId === undefined) {
                        if (!data?.redacted) {
                            return;
                        }
                        entryId = this._addText<UiThinkingEntry>(
                            { role: 'thinking', text: '', redacted: true },
                            parentId,
                        );
                    }
                    this._mutate(() =>
                        this._transcript.update<UiThinkingEntry>(entryId, (e) => ({
                            ...e,
                            streaming: false,
                            durationMs: e.startedAt ? Date.now() - e.startedAt : 0,
                        })),
                    );
                    this._thinkingMsgs.delete(parentId);
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<CliErrorNotification>(Msg.toWebView.cli.error, (data) => {
                this._addText({ role: 'error', text: data?.message ?? 'Unknown error' });
                // An error can end the turn without a `result`; free the UI so it doesn't stay busy.
                appState.isBusy = false;
            }),
        );

        this._offs.push(
            bridge.onNotification<ExchangeEndedNotification>(
                Msg.toWebView.chat.exchangeEnded,
                (data) => {
                    // A turn can fail with nothing to show for it: max_turns, budget exhausted or a
                    // refusal produce no failing tool row, so without this the stream just stops and
                    // the user is left guessing. A visible notice is the only place this reaches them.
                    // …but a turn the user stopped is not one of those: the transcript already
                    // carries "[Request interrupted by user]", so a red notice under it would
                    // report their own decision back to them as a failure.
                    if (data?.isError && !isUserAbort(data.errorKind ?? '')) {
                        const label = turnErrorLabel(data.errorKind ?? '');
                        const detail = turnErrorDetail(data.errorText ?? '');
                        this._addText<UiSlashResultEntry>({
                            role: 'slash-result',
                            text: detail ? `${label} — ${detail}` : label,
                            isError: true,
                        });
                    }
                    // Cost/tokens/duration are not a transcript entry: they belong to the exchange,
                    // not to the conversation, so they ride the hover actions row instead of taking
                    // a permanent line under every answer. Keyed by the last entry id of the turn —
                    // exchanges are derived on the fly and have no id of their own, but that entry
                    // is the one the row renders against.
                    if (appState.ui.showCostAndDuration && data?.durationMs != null) {
                        // Same key the row reads: the turn's last text block, not the last entry —
                        // a turn ending on a tool row would otherwise file the figures under an id
                        // nothing renders against.
                        const lastText = [...this._transcript.entries]
                            .reverse()
                            .find(
                                (e) =>
                                    e.kind === 'text' &&
                                    (e.role === 'assistant' || e.role === 'slash-result'),
                            );
                        if (lastText) {
                            this._turnMetrics.set(lastText.id, {
                                costUsd: data.costUsd ?? 0,
                                durationMs: data.durationMs,
                                usage:
                                    data.usage ?? this._turnMetrics.get(lastText.id)?.usage ?? null,
                            });
                        }
                    }
                    if (this._streamingMsgs.size > 0) {
                        const ids = [...this._streamingMsgs.values()];
                        this._mutate(() =>
                            this._transcript.updateMany(ids, (e) =>
                                'streaming' in e ? { ...e, streaming: false } : e,
                            ),
                        );
                        this._streamingMsgs.clear();
                    }
                },
            ),
        );

        this._offs.push(
            bridge.onNotification(Msg.toWebView.chat.cleared, () => {
                // Abort in-flight requests for the old session so their Promises reject
                // instead of resolving against the new session (or hanging until timeout).
                bridge.rejectAllPending('session changed');
                // clear() fires onCleared, which drops everything keyed on the old entry ids.
                this._mutate(() => this._transcript.clear());
                // The rendered markdown belonged to the entries just dropped; keeping it would let
                // a dead session's messages hold the cache slots the new one needs.
                clearMarkdownCache();
                // The transcript this turn belonged to is gone, so its `result` — the only thing
                // that clears busy — would land on nothing. Without this the spinner outlives the
                // session that started it, with no message under it to explain what it is waiting for.
                appState.isBusy = false;
                // Anything queued was written for the session that just went; the isBusy above is
                // what would otherwise flush it into the new one.
                this.querySelector('cv-prompt')?.dropQueue();
                appState.currentSessionId = null;
                appState.oldestLoadedOffset = -1;
                appState.hasMoreHistory = false;
                appState.loadingOlder = false;
                appState.contextUsage = null;
                // The CLI's advisories are about the session that just went away ("session model
                // could not be restored"), so they'd read as stale against the new one. The dead-CLI
                // row is about the process instead: it survives, and cli_started clears it.
                this._systemNotices?.dismissExcept(CLI_EXITED_KEY);
            }),
        );

        this._offs.push(
            bridge.onNotification<CompactedNotification>(Msg.toWebView.chat.compacted, (data) => {
                this._appendEntry(CvApp.buildCompactEntry(data));
                queueMicrotask(() => this._scrollToBottom());
            }),
        );

        this._offs.push(
            bridge.onNotification<EvictMessagesNotification>(
                Msg.toWebView.chat.evictMessages,
                (data) => {
                    // The CLI retracted these: they reached us, but the model does not have them.
                    // Leaving them up is what makes the transcript diverge from the model's context.
                    const uuids = data?.uuids ?? [];
                    if (uuids.length === 0) {
                        return;
                    }
                    // The maps keyed on entry ids are cleaned by the transcript's onRemoved.
                    this._mutate(() => this._transcript.removeByUuid(uuids));
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<StatusNotification>(Msg.toWebView.chat.status, (data) => {
                // Raw CLI work status; the spinner maps known values to a label (e.g. compacting).
                appState.status = data?.status ?? '';
                // A failed compaction otherwise ends in silence — the spinner just stops and the
                // chat is left un-compacted with no clue why. Surface it as a red centered notice.
                if (data?.compactResult === 'failed') {
                    const why = data.compactError ? ` — ${data.compactError}` : '';
                    this._addText<UiSlashResultEntry>({
                        role: 'slash-result',
                        text: `Compaction failed${why}`,
                        isError: true,
                    });
                }
            }),
        );

        this._offs.push(
            bridge.onNotification<ModelChangedNotification>(
                Msg.toWebView.cli.modelChanged,
                (data) => {
                    const m = data?.model ?? '';
                    if (!m) {
                        return;
                    }
                    appState.currentModel = m;
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ToolPermissionNotification>(
                Msg.toWebView.chat.toolPermission,
                (data) => {
                    if (!data?.id || !data?.name) {
                        return;
                    }
                    // Gauge update (for assistant messages with only tool_use, no text).
                    if (!data.parentToolUseId && data.usage) {
                        appState.contextUsage = data.usage;
                    }
                    // Dedup by toolUseId (arrives twice: can_use_tool + assistant msg).
                    const existing = this._transcript.findTool(data.id);
                    if (existing) {
                        this._mutate(() =>
                            this._transcript.update<UiToolEntry>(existing.id, (e) => ({
                                ...e,
                                data: {
                                    id: data.id,
                                    name: data.name,
                                    input: (data.input ?? {}) as Record<string, unknown>,
                                },
                            })),
                        );
                        return;
                    }
                    this._appendEntry(this.buildToolEntry(data), data.parentToolUseId ?? undefined);
                    queueMicrotask(() => this._scrollToBottom());
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ToolResultNotification>(Msg.toWebView.chat.toolResult, (data) => {
                if (!data?.toolUseId) {
                    return;
                }
                const tool = this._transcript.findTool(data.toolUseId);
                if (!tool) {
                    return;
                }
                const atBottom = this._isNearBottom();
                this._mutate(() =>
                    this._transcript.update<UiToolEntry>(tool.id, (e) =>
                        CvApp.applyToolResult(e, data),
                    ),
                );
                // The result grows the tool row (e.g. an answered ask); follow it down so the
                // view doesn't stay stuck on the tool until the next message arrives.
                if (atBottom) {
                    queueMicrotask(() => this._scrollToBottom());
                }
            }),
        );

        this._offs.push(
            bridge.onNotification<ToolProgressNotification>(
                Msg.toWebView.chat.toolProgress,
                (data) => {
                    if (!data?.toolUseId) {
                        return;
                    }
                    const tool = this._transcript.findTool(data.toolUseId);
                    if (!tool) {
                        return;
                    }
                    // Wire field is elapsedSeconds (from the host); the entry keeps it as
                    // elapsedSec (internal UI state).
                    this._mutate(() =>
                        this._transcript.update<UiToolEntry>(tool.id, (e) => ({
                            ...e,
                            elapsedSec: data.elapsedSeconds ?? 0,
                        })),
                    );
                },
            ),
        );

        // Unprompted history push (session open/resume, CLI respawn, settings re-render).
        // The scroll-up path is a correlated getHistory request handled in _loadOlderHistory.
        this._offs.push(
            bridge.onNotification<HistoryLoadedNotification>(
                Msg.toWebView.chat.historyLoaded,
                (data) => {
                    const events = data?.events ?? [];
                    // Before the replay, not after: a history push replaces the tree, so the ids
                    // these were keyed on are gone — but building the replacement is what refills
                    // them for the replayed turns, and clearing afterwards would drop that. This is
                    // why replaceAll leaves onCleared alone and the drop happens here instead.
                    this._dropAllIds();
                    const out = this._applyHistoryPage(data, events);

                    // Seed the gauge from the last assistant_text event carrying usage.
                    for (let i = events.length - 1; i >= 0; i--) {
                        if (events[i].type === Msg.toWebView.chat.assistantText) {
                            const u = (events[i].data as AssistantTextNotification).usage;
                            if (u) {
                                appState.contextUsage = u;
                                break;
                            }
                        }
                    }

                    // The context WINDOW (maxTokens) only ships in exchangeEnded (turn end), so on a
                    // reload with no turn the gauge stays hidden (_window<=0). Ask the CLI on-demand:
                    // get_context_usage returns maxTokens for the current model, provider-agnostic.
                    if (appState.contextWindow <= 0) {
                        fetchContextUsage()
                            .then((d) => {
                                if (d?.maxTokens > 0) {
                                    appState.contextWindow = d.maxTokens;
                                }
                            })
                            .catch(() => {
                                /* gauge just stays hidden until the first turn */
                            });
                    }

                    appState.loadingOlder = false;
                    this._mutate(() => this._transcript.replaceAll(out));
                    // Land at the bottom, sustained for a few frames: a whole page of transcript
                    // just went in, and images (the one thing rendered async) settle after it.
                    this._scrollToBottom('instant', 6);
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<SubagentStartedNotification>(
                Msg.toWebView.chat.subagentStarted,
                (d) => {
                    if (!d?.taskId) {
                        return;
                    }
                    const m = new Map(this._subagentTasks);
                    m.set(d.taskId, {
                        taskId: d.taskId,
                        description: d.description ?? '',
                        toolUseId: d.toolUseId ?? undefined,
                        recentTools: [],
                        usage: { totalTokens: 0, toolUses: 0, durationMs: 0 },
                        startedAt: Date.now(),
                    });
                    this._subagentTasks = m;
                    this._publishSubagentTasks(m);
                    // The Agent row is usually created after this and reads the id in
                    // buildToolEntry; when it got in first, tag it here. Nothing marks the view
                    // stale — the row was just appended, so its render is still pending and will
                    // read the id.
                    const row = d.toolUseId ? this._transcript.findTool(d.toolUseId) : null;
                    if (row && !row.agentId) {
                        this._mutate(() =>
                            this._transcript.update<UiToolEntry>(row.id, (e) => ({
                                ...e,
                                agentId: d.taskId,
                            })),
                        );
                    }
                },
            ),
        );
        this._offs.push(
            bridge.onNotification<SubagentProgressNotification>(
                Msg.toWebView.chat.subagentProgress,
                (d) => {
                    if (!d?.taskId) {
                        return;
                    }
                    const prev = this._subagentTasks.get(d.taskId);
                    if (!prev) {
                        return;
                    }
                    const last = d.lastToolName;
                    const recentTools =
                        last && last !== prev.recentTools[prev.recentTools.length - 1]
                            ? [...prev.recentTools, last].slice(-3)
                            : prev.recentTools;
                    const m = new Map(this._subagentTasks);
                    m.set(d.taskId, {
                        ...prev,
                        description: d.description || prev.description,
                        recentTools,
                        toolUseId: d.toolUseId ?? prev.toolUseId,
                        summary: d.summary ?? prev.summary,
                        usage: d.usage ?? prev.usage,
                    });
                    this._subagentTasks = m;
                    this._publishSubagentTasks(m);
                },
            ),
        );
        this._offs.push(
            bridge.onNotification<SubagentEndedNotification>(
                Msg.toWebView.chat.subagentEnded,
                (d) => {
                    if (!d?.taskId || !this._subagentTasks.has(d.taskId)) {
                        return;
                    }
                    // The Agent row's own tool_result is launch metadata — it arrives at once and is
                    // never is_error, so the row would settle green however the sub-agent ended. This
                    // notification carries the real outcome; 'stopped' is a cancellation the user
                    // asked for, so only 'failed' turns the row red.
                    const toolUseId = this._subagentTasks.get(d.taskId)?.toolUseId;
                    if (d.status === 'failed' && toolUseId) {
                        const row = this._transcript.findTool(toolUseId);
                        if (row) {
                            this._mutate(() =>
                                this._transcript.update<UiToolEntry>(row.id, (e) => ({
                                    ...e,
                                    status: 'error',
                                })),
                            );
                        }
                    }
                    const m = new Map(this._subagentTasks);
                    m.delete(d.taskId);
                    this._subagentTasks = m;
                    this._publishSubagentTasks(m);
                },
            ),
        );
        this._offs.push(
            bridge.onNotification(Msg.toWebView.chat.subagentClear, () => {
                if (this._subagentTasks.size > 0) {
                    this._subagentTasks = new Map();
                    appState.subagentTasks = [];
                }
            }),
        );
    }

    override firstUpdated(): void {
        // Scroll listener drives lazy loading of older messages. Attached
        // imperatively to keep a single subscription tied to the host lifecycle.
        this._messagesEl?.addEventListener('scroll', this._onMessagesScroll, { passive: true });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._messagesEl?.removeEventListener('scroll', this._onMessagesScroll);
        if (this._jumpRaf !== 0) {
            cancelAnimationFrame(this._jumpRaf);
            this._jumpRaf = 0;
        }
        window.removeEventListener('keydown', this._onGlobalEsc);
        this.removeEventListener('subagent-toggle', this._onChildrenToggle as EventListener);
        this.removeEventListener('compact-expand', this._onCompactExpand as EventListener);
        this.removeEventListener('model-switched', this._onModelSwitched as EventListener);
        this.removeEventListener('queued-sent', this._onQueuedSent as EventListener);
        this.removeEventListener('queued-dropped', this._onQueuedDropped as EventListener);
        for (const off of this._offs) {
            off();
        }
        this._offs.length = 0;
    }

    /** Esc anywhere interrupts generation — unless a permission/Ask prompt is
     *  open (the banner consumes Esc) or a menu/popover is open (model,
     *  permission-mode, @ menu — there Esc just closes the menu, handled by the
     *  native Popover API). */
    private _onGlobalEsc = (e: KeyboardEvent): void => {
        if (e.key !== 'Escape' || this._awaitingUser || !this._isBusy) {
            return;
        }
        // A native popover (model/permission/@ menu) is open → let Esc close it.
        if (document.querySelector(':popover-open')) {
            return;
        }
        e.preventDefault();
        // Same gesture as the Stop button, so it goes through the composer's own stop: doing the
        // interrupt here and nothing else left the queue intact, and the next flush then sent
        // prompts the user had written behind a turn they had just cancelled.
        this.querySelector('cv-prompt')?.stop();
    };

    /** Build UiEntry[] from a replayed page of typed events (chat_history / subagent_loaded).
     *  Uses the SAME build* as the live handlers; only the placement differs (accumulate here,
     *  append/prepend in the caller). Parent-bucket + post-pass = order-independent nesting. */
    private _replayEvents(events: HistoryEventDto[]): UiEntry[] {
        const out: UiEntry[] = [];
        const childrenByParent = new Map<string, UiEntry[]>();
        const place = (entry: UiEntry, parentId?: string | null): void => {
            if (parentId) {
                const bucket = childrenByParent.get(parentId) ?? [];
                bucket.push(entry);
                childrenByParent.set(parentId, bucket);
            } else {
                out.push(entry);
            }
        };
        const findTool = (id: string): UiToolEntry | null => {
            const top = out.find((e): e is UiToolEntry => e.kind === 'tool' && e.toolUseId === id);
            if (top) {
                return top;
            }
            for (const bucket of childrenByParent.values()) {
                const b = bucket.find(
                    (e): e is UiToolEntry => e.kind === 'tool' && e.toolUseId === id,
                );
                if (b) {
                    return b;
                }
            }
            return null;
        };

        for (const ev of events ?? []) {
            switch (ev.type) {
                case Msg.toWebView.chat.userText: {
                    const d = ev.data as UserTextNotification;
                    const e = CvApp.buildUserEntry(d);
                    if (e) {
                        place(e, d.parentToolUseId);
                    }
                    break;
                }
                case Msg.toWebView.chat.assistantText: {
                    const d = ev.data as AssistantTextNotification;
                    if (d.text?.trim()) {
                        const entry = CvApp.buildAssistantEntry(d);
                        place(entry, d.parentToolUseId);
                        this._seedTurnTokens(entry.id, d);
                    }
                    break;
                }
                case Msg.toWebView.chat.toolPermission: {
                    const d = ev.data as ToolPermissionNotification;
                    if (d.id && d.name) {
                        place(this.buildToolEntry(d), d.parentToolUseId);
                    }
                    break;
                }
                case Msg.toWebView.chat.toolResult: {
                    const d = ev.data as ToolResultNotification;
                    const hit = d.toolUseId ? findTool(d.toolUseId) : null;
                    if (hit) {
                        // Replay builds a fresh list nothing is rendering yet, so writing into
                        // the entry here is safe — it reaches the tree already folded.
                        Object.assign(hit, CvApp.applyToolResult(hit, d));
                    }
                    break;
                }
                case Msg.toWebView.chat.compacted: {
                    place(CvApp.buildCompactEntry(ev.data as CompactedNotification));
                    break;
                }
                default:
                    break;
            }
        }

        // A tool_use with no matching tool_result on disk was never completed — the session
        // ended while it was open (e.g. an AskUserQuestion the user closed without answering).
        // In replay nothing more is coming, so mark it interrupted (static red dot) instead of
        // leaving it 'pending' — a spinning "in progress" dot that would never resolve.
        for (const e of out) {
            if (e.kind === 'tool' && e.status === 'pending') {
                e.status = 'error';
            }
        }
        for (const bucket of childrenByParent.values()) {
            for (const e of bucket) {
                if (e.kind === 'tool' && e.status === 'pending') {
                    e.status = 'error';
                }
            }
        }

        // Post-pass: attach the kept ≤3 children under each parent Agent tool row.
        if (childrenByParent.size > 0) {
            for (const e of out) {
                if (e.kind !== 'tool') {
                    continue;
                }
                let children = childrenByParent.get(e.toolUseId);
                if (children?.length) {
                    // The sub-agent's first message echoes the launch prompt, already shown as
                    // the Agent row's IN — drop it here too, matching the live path (_appendEntry).
                    // History replay bypasses that filter, so without this the echo reappears as
                    // a user bubble when a session is reopened.
                    const prompt = String(e.data?.input?.prompt ?? '').trim();
                    if (prompt) {
                        children = children.filter(
                            (c) =>
                                !(
                                    c.kind === 'text' &&
                                    c.role === 'user' &&
                                    c.text?.trim() === prompt
                                ),
                        );
                    }
                    e.children = {
                        items: children.slice(-3),
                        hasMore: children.length > 3,
                        showAll: false,
                    };
                }
            }
        }
        return out;
    }

    /** Scroll-up trigger: within 200px of the top, with older content and no
     *  in-flight fetch, request the next batch. The generous threshold lets
     *  the page land before the very top, avoiding flicker. */
    private _onMessagesScroll = (): void => {
        const el = this._messagesEl;
        if (!el) {
            return;
        }
        // Before the lazy-load guards below: those return early on most scrolls, and the jump
        // button has to follow every one of them. This listener is already passive — a second one
        // for the same event is what we are avoiding.
        this._queueJumpUpdate();
        if (!appState.hasMoreHistory || appState.loadingOlder) {
            return;
        }
        if (!appState.currentSessionId || appState.oldestLoadedOffset < 0) {
            return;
        }
        if (el.scrollTop > 200) {
            return;
        }
        appState.loadingOlder = true;
        const reqSession = appState.currentSessionId;
        bridge
            .sendRequest(GetHistoryReq, {
                sessionId: appState.currentSessionId,
                beforeOffset: appState.oldestLoadedOffset,
            })
            .then((data) => {
                // Drop the page if the user switched session mid-fetch (the response is stale).
                if (data?.sessionId && data.sessionId !== appState.currentSessionId) {
                    appState.loadingOlder = false;
                    return;
                }
                const out = this._applyHistoryPage(data, data?.events ?? []);
                appState.loadingOlder = false;
                this._prependWithAnchor(out);
            })
            .catch(() => {
                if (appState.currentSessionId === reqSession) {
                    appState.loadingOlder = false;
                }
            });
    };

    // Generic over the concrete text-entry type: call sites pass the type argument explicitly
    // (e.g. _addText<UiAssistantEntry>({role:'assistant', …})) so `Omit` works on a single member
    // — Omit over the whole union would collapse to the common keys and drop the role-specific ones.
    /** Returns the id, not the entry: the transcript replaces an entry on every change, so a
     *  reference kept by the caller would name something the tree no longer holds. */
    private _addText<E extends Extract<UiEntry, { kind: 'text' }>>(
        msg: Omit<E, 'kind' | 'id'>,
        parentId = '',
    ): number {
        const entry = { kind: 'text', id: ++_entryIdSeq, ...msg } as E;
        this._appendEntry(entry, parentId);
        queueMicrotask(() => this._scrollToBottom());
        return entry.id;
    }

    /** Append `entry` under the tool row `parentId`, or to the root list when there is no parent
     *  or it isn't in the tree. The ring of three and the upsert live in Transcript.appendChild;
     *  what stays here is the one thing that is a rendering decision — dropping the sub-agent's
     *  echo of its own launch prompt. */
    private _appendEntry(entry: UiEntry, parentId?: string): void {
        if (parentId) {
            const parent = this._transcript.findTool(parentId);
            if (parent) {
                // The sub-agent's first message echoes the prompt the Agent tool was
                // launched with — it's already shown as the Agent row's IN, so drop the duplicate.
                if (
                    entry.kind === 'text' &&
                    entry.role === 'user' &&
                    entry.text?.trim() === String(parent.data?.input?.prompt ?? '').trim()
                ) {
                    return;
                }
                this._mutate(() => this._transcript.appendChild(parentId, entry, CvApp._childKey));
                return;
            }
        }
        this._mutate(() => this._transcript.append(entry));
    }

    /** Expand/collapse a nested Agent box (CustomEvent from cv-tool-row). */
    private _onChildrenToggle = (
        e: CustomEvent<{ agentId: string; expand: boolean; preview?: boolean }>,
    ): void => {
        const { agentId, expand, preview } = e.detail ?? {};
        const parent = agentId ? this._transcript.findToolByAgentId(agentId) : null;
        if (!parent) {
            return;
        }
        // A history Agent may not have fetched anything yet.
        const kids = parent.children ?? { items: [], hasMore: false, showAll: false };
        if (expand) {
            // Rule: history FETCHES, live SHOWS what it already has in memory.
            //  - preview (first chevron expand): fetch only if children are empty (history). Live
            //    already streamed them in → no fetch.
            //  - Show all (preview=false): mark showAll; fetch the whole transcript only if there's
            //    more than we hold (hasMore = a history preview). Live/already-full → just show all.
            // The CLI writes the transcript as the agent runs, so a fetch mid-run returns
            // everything that has happened so far — no need to special-case a running agent.
            const showAll = !preview;
            // Show all sets the flag; preview (chevron open) leaves it false → renderChildren shows ≤3.
            // Row open/closed is the component's own `_expanded`, not tracked here.
            this._mutate(() =>
                this._transcript.update<UiToolEntry>(parent.id, (e) => ({
                    ...e,
                    children: { ...(e.children ?? kids), showAll },
                })),
            );

            const needFetch = preview
                ? kids.items.length === 0 // history preview
                : kids.hasMore; // Show all with more on disk than we hold
            if (!needFetch) {
                return;
            }
            fetchSubagent(agentId, { preview: !!preview })
                .then((data) => {
                    const p = this._transcript.findToolByAgentId(data.agentId);
                    if (!p) {
                        return;
                    }
                    const pk = p.children ?? { items: [], hasMore: false, showAll: false };
                    const full = this._replayEvents(data.events ?? []);
                    // The transcript opens and closes with what the Agent row already shows in its
                    // own cells: the launch prompt (IN) and the closing report (OUT, from the
                    // tool_result the parent recorded). Both would read as duplicates among the
                    // sub-agent's steps. Their events carry no parentToolUseId, so the
                    // _replayEvents post-pass filter doesn't reach them — drop them here.
                    const prompt = String(p.data?.input?.prompt ?? '').trim();
                    const report = (p.result ?? '').trim();
                    // Show all replaces the kept ≤3 with the whole transcript, in file order.
                    // Cleared HERE, not before the fetch: an empty list hides the Show all button
                    // (componentHeaderActions), so the row would lose its control mid-flight.
                    let list = showAll ? [] : pk.items;
                    for (const child of full) {
                        if (child.kind === 'text') {
                            const t = child.text?.trim();
                            if (prompt && child.role === 'user' && t === prompt) {
                                continue;
                            }
                            if (report && child.role === 'assistant' && t === report) {
                                continue;
                            }
                        }
                        list = this._upsertChild(list, child);
                    }
                    // Preview keeps the last 3 (and flags "…" if more); full keeps everything and is
                    // now complete in memory (hasMore=false → no more fetches).
                    const items = preview ? list.slice(-3) : list;
                    this._mutate(() =>
                        this._transcript.update<UiToolEntry>(p.id, (e) => ({
                            ...e,
                            children: {
                                items,
                                hasMore: preview ? list.length > 3 : false,
                                showAll: !preview,
                            },
                        })),
                    );
                })
                .catch(() => {
                    /* timeout / not found — leave the kept children as-is */
                });
        } else {
            // "Reduce" (Show all → off): show the last 3 again, but KEEP the full list in memory so a
            // later Show all doesn't refetch (and live never can). renderChildren slices the view.
            // hasMore reflects that more is held than shown.
            this._mutate(() =>
                this._transcript.update<UiToolEntry>(parent.id, (e) => {
                    const c = e.children ?? kids;
                    return {
                        ...e,
                        children: { ...c, showAll: false, hasMore: c.items.length > 3 },
                    };
                }),
            );
        }
    };

    /** A compact separator's <details> opened for the first time: fetch the summary lazily
     *  and cache it (`loaded` gates a refetch on collapse/re-expand). */
    private _onCompactExpand = (e: CustomEvent<{ uuid: string }>): void => {
        const { uuid } = e.detail ?? {};
        if (!uuid) {
            return;
        }
        const entry = this._transcript.entries.find(
            (en): en is UiCompactEntry =>
                en.kind === 'text' && en.role === 'compact' && en.uuid === uuid,
        );
        if (!entry || entry.loaded) {
            return;
        }
        fetchCompactSummary(appState.currentSessionId, uuid)
            .then((res) => {
                this._mutate(() =>
                    this._transcript.update<UiCompactEntry>(entry.id, (en) => ({
                        ...en,
                        summary: res.summary,
                        loaded: true,
                    })),
                );
            })
            .catch(() => {
                /* timeout / not found — leave "Loading…" as-is */
            });
    };

    /** User picked a model from the menu (cv-prompt): show "Switched to X" — but only
     *  during a live chat, not on an empty transcript (nothing above it would be noise). */
    private _onModelSwitched = (e: CustomEvent<{ value: string }>): void => {
        const value = e.detail?.value;
        if (!value || this._transcript.entries.length === 0) {
            return;
        }
        this._addText({ role: 'status', text: `Switched to ${modelLabel(value)}` });
    };

    /**
     * A queued message went to the CLI: move its bubble below the reply it had been sitting above,
     * so that reply keeps the question which actually prompted it. The fading is already handled —
     * the uuid left `appState.queuedUuids` — so only the position is left.
     *
     * No scroll: the user may well be reading further up, often the very reason they queued
     * something, and the jump button already covers going back down.
     */
    private _onQueuedSent = (e: CustomEvent<{ uuid: string }>): void => {
        const uuid = e.detail?.uuid;
        if (uuid) {
            this._mutate(() => this._transcript.moveToEnd(uuid));
        }
    };

    /** Stop or Clear: these never reached the CLI, so the model has no idea they exist. Leaving
     *  them on screen is the same divergence a retraction causes, from our own side. */
    private _onQueuedDropped = (e: CustomEvent<{ uuids: string[] }>): void => {
        const uuids = e.detail?.uuids ?? [];
        if (uuids.length > 0) {
            this._mutate(() => this._transcript.removeByUuid(uuids));
        }
    };

    /** Stable identity of a sub-agent child: toolUseId for tools, uuid for text. */
    private static _childKey(e: UiEntry): string {
        if (e.kind === 'tool') {
            return e.toolUseId;
        }
        return ('uuid' in e ? e.uuid : undefined) ?? `t${e.id}`;
    }

    // Pure DTO → UiEntry builders, shared by the live handlers (then append)
    // and the history replay (then batch/prepend). Zero side effects: no _entries,
    // no _appendEntry, no scroll, no gauge. The one construction path for both.

    /** UserTextNotification → a user or slash-result entry (with lazy image/file chips), or null when
     *  it's a sub-agent tool-result echo / meta-injection / empty envelope that shouldn't render. */
    private static buildUserEntry(
        d: UserTextNotification,
    ): UiUserEntry | UiSlashResultEntry | null {
        if (d.parentToolUseId && !d.text?.startsWith('[Request interrupted')) {
            return null;
        }
        const text = d.text ?? '';
        // A slash command's local output (<local-command-stdout>/stderr>) is its own role — a centered
        // pill, not a user bubble. Empty output (e.g. the /model picker) renders nothing.
        const lco = parseLocalCommandOutput(text);
        if (lco) {
            return lco.text
                ? {
                      kind: 'text',
                      id: ++_entryIdSeq,
                      role: 'slash-result',
                      text: lco.text,
                      isError: lco.isError,
                      timestamp: d.timestamp ?? undefined,
                  }
                : null;
        }
        const images: UiImage[] = (d.images ?? []).map((img) => ({
            name: 'image',
            lazy: img.uuid ? { uuid: img.uuid, blockIdx: img.blockIdx } : undefined,
            preview: img.preview ?? undefined,
        }));
        const files: UiFile[] = (d.files ?? []).map((f) => ({
            name: f.name ?? 'file',
            lazy: f.uuid ? { uuid: f.uuid, blockIdx: f.blockIdx } : undefined,
        }));
        // CLI meta-injections are filtered host-side (ContentBlockTranslator via MetaInjection),
        // so anything that reaches here is a real user turn.
        if (!text && images.length === 0 && files.length === 0) {
            return null;
        }
        return {
            kind: 'text',
            id: ++_entryIdSeq,
            role: 'user',
            text,
            uuid: d.uuid ?? undefined,
            images: images.length > 0 ? images : undefined,
            files: files.length > 0 ? files : undefined,
            timestamp: d.timestamp ?? undefined,
        };
    }

    /** AssistantTextNotification → an assistant text entry. */
    private static buildAssistantEntry(d: AssistantTextNotification): UiAssistantEntry {
        return {
            kind: 'text',
            id: ++_entryIdSeq,
            role: 'assistant',
            text: d.text ?? '',
            timestamp: d.timestamp ?? undefined,
            uuid: d.uuid ?? undefined,
            error: d.error ?? undefined,
        };
    }

    /** CompactedNotification → the compact separator. Header-only: the summary text is fetched
     *  lazily on first expand (compact-expand listener) and cached via `loaded`. */
    private static buildCompactEntry(d: CompactedNotification | null): UiCompactEntry {
        return {
            kind: 'text',
            id: ++_entryIdSeq,
            role: 'compact',
            text: '',
            uuid: d?.uuid ?? '',
            trigger: d?.trigger ?? 'auto',
            preTokens: d?.preTokens ?? 0,
            loaded: false,
        };
    }

    /** ToolPermissionNotification → a pending tool row. An Agent row takes the id of the
     *  sub-agent it launched: task_started names both ids and lands just before this, whereas
     *  the result only carries agentId in history — live it is null, and without it the row has
     *  no transcript to open. */
    private buildToolEntry(d: ToolPermissionNotification): UiToolEntry {
        const input = (d.input ?? {}) as Record<string, unknown>;
        const spawned = [...this._subagentTasks.values()].find((t) => t.toolUseId === d.id);
        return {
            kind: 'tool',
            id: ++_entryIdSeq,
            toolUseId: d.id,
            data: { id: d.id, name: d.name, input },
            status: 'pending',
            result: '',
            fullLineCount: 0,
            elapsedSec: 0,
            agentId: spawned?.taskId,
        };
    }

    /** Fold a ToolResultNotification into a tool row, returning a new entry. */
    private static applyToolResult(e: UiToolEntry, d: ToolResultNotification): UiToolEntry {
        return {
            ...e,
            status: d.isError ? 'error' : 'done',
            result: d.result ?? '',
            fullLineCount: d.fullLineCount ?? 0,
            ...(d.agentId ? { agentId: d.agentId } : {}),
            // Only written when the tool reported something: an interrupted Agent sends none, and
            // an empty object here would replace the running badge with a blank one.
            ...(d.extras ? { extras: d.extras } : {}),
        };
    }

    /** Upsert `entry` into `list` by child key: replace the matching entry (keeps the
     *  freshest version) or append a new one. Returns a new array (Lit reactivity). */
    private _upsertChild(list: UiEntry[], entry: UiEntry): UiEntry[] {
        const key = CvApp._childKey(entry);
        const i = list.findIndex((e) => CvApp._childKey(e) === key);
        if (i < 0) {
            return [...list, entry];
        }
        const next = [...list];
        next[i] = entry;
        return next;
    }

    /** Publish the running sub-agents, each linked to the task that launched it and ordered so a
     *  child follows its parent. The wire has no parent link — task_started only names the Agent
     *  row — so it is read off the tree, where nesting is the row's position. Recomputed on every
     *  update: a task can beat its own row by a few ms and would otherwise stay flat. */
    private _publishSubagentTasks(tasks: Map<string, SubagentTask>): void {
        // The Agent row enclosing the one that launched this task. Descends carrying the current
        // container, so finding the row IS finding its parent.
        const enclosingToolOf = (toolUseId?: string): string | undefined => {
            if (!toolUseId) {
                return undefined;
            }
            const walk = (
                list: readonly UiEntry[],
                container?: string,
            ): string | undefined | false => {
                for (const e of list) {
                    if (e.kind !== 'tool') {
                        continue;
                    }
                    if (e.toolUseId === toolUseId) {
                        return container;
                    }
                    if (e.children?.items.length) {
                        const hit = walk(e.children.items, e.toolUseId);
                        if (hit !== false) {
                            return hit;
                        }
                    }
                }
                return false; // not in this branch — distinct from "found, no container"
            };
            const hit = walk(this._transcript.entries);
            return hit === false ? undefined : hit;
        };

        const byToolUse = new Map<string, string>();
        for (const t of tasks.values()) {
            if (t.toolUseId) {
                byToolUse.set(t.toolUseId, t.taskId);
            }
        }
        const linked = [...tasks.values()].map((t) => {
            const parentTool = enclosingToolOf(t.toolUseId);
            return { ...t, parentTaskId: parentTool ? byToolUse.get(parentTool) : undefined };
        });
        // Depth-first: every task is emitted right after its parent, so the indent in the popover
        // lines up with who launched whom.
        const childrenOf = (parentTaskId?: string) =>
            linked.filter((t) => t.parentTaskId === parentTaskId);
        const flatten = (parentTaskId?: string): SubagentTask[] =>
            childrenOf(parentTaskId).flatMap((t) => [t, ...flatten(t.taskId)]);
        const ordered = flatten(undefined);
        // A task whose parent is gone (ended while the child runs) would vanish from the walk —
        // keep it, at top level.
        const seen = new Set(ordered.map((t) => t.taskId));
        appState.subagentTasks = [...ordered, ...linked.filter((t) => !seen.has(t.taskId))];
    }

    /** True when scrolled at/near the bottom. Gates stream auto-follow so
     *  the user isn't yanked down while reading scrolled-up content. */
    /** Coalesce to one measurement per frame: a scroll fires far more often than it repaints, and
     *  reading scrollHeight on each event is a forced layout for a value that can't have changed
     *  twice in a frame. */
    private _queueJumpUpdate(): void {
        if (this._jumpRaf !== 0) {
            return;
        }
        this._jumpRaf = requestAnimationFrame(() => {
            this._jumpRaf = 0;
            this._updateJump();
        });
    }

    /** Two thresholds, not one: during streaming the distance to the bottom oscillates as content
     *  lands, and a single line would flicker the button in and out on every delta. It takes a
     *  real scroll of 300px to show it, and coming back within 120px to hide it again. */
    private _updateJump(): void {
        const el = this._messagesEl;
        // clientHeight reads 0 while WebView2 is suspended in the background; a distance measured
        // then is meaningless, so leave the button as it is rather than acting on a false.
        if (!el || el.clientHeight === 0) {
            return;
        }
        const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
        if (!this._showJump && distance > 300) {
            this._showJump = true;
        } else if (this._showJump && distance < 120) {
            this._showJump = false;
        }
    }

    private _isNearBottom(threshold = 80): boolean {
        const el = this._messagesEl;
        if (!el) {
            return true;
        }
        return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
    }

    /** Shared history-page processing for both the unprompted load (chat_history_loaded) and
     *  the scroll-up response (getHistory): replay the events to UiEntry and update the paging
     *  state (currentSessionId / oldestLoadedOffset / hasMoreHistory). Returns the replayed list;
     *  the caller decides whether to replace (initial) or prepend (scroll). */
    private _applyHistoryPage(
        data: { sessionId?: string | null; oldestOffset?: number; hasMore?: boolean } | undefined,
        events: HistoryEventDto[],
    ): UiEntry[] {
        const out = this._replayEvents(events);
        if (data?.sessionId) {
            appState.currentSessionId = data.sessionId;
        }
        if (typeof data?.oldestOffset === 'number') {
            appState.oldestLoadedOffset = data.oldestOffset;
        }
        appState.hasMoreHistory = !!data?.hasMore;
        return out;
    }

    /**
     * Prepend a page of older history while keeping the user's reading row
     * fixed. We anchor on the DISTANCE FROM THE BOTTOM (`scrollHeight -
     * scrollTop`), which is invariant to content growing both above (the
     * prepended page) and below (async-rendered markdown / diff2html / lazy
     * images) the viewport — unlike a one-shot `scrollTop += delta`, which
     * slides as async children settle. A ResizeObserver keeps re-applying the
     * anchor until the list height stops changing, so there are no magic frame
     * counts. `scroll-behavior` is forced to `auto` during the operation so the
     * imperative `scrollTop` writes aren't swallowed by the smooth animation.
     */
    private _prependWithAnchor(older: UiEntry[]): void {
        const el = this._messagesEl;
        if (!el) {
            this._mutate(() => this._transcript.prepend(older));
            appState.loadingOlder = false;
            return;
        }
        const distFromBottom = el.scrollHeight - el.scrollTop;
        const prevBehavior = el.style.scrollBehavior;
        el.style.scrollBehavior = 'auto';

        const anchor = (): void => {
            el.scrollTop = el.scrollHeight - distFromBottom;
        };

        this._mutate(() => this._transcript.prepend(older));
        // Commit the DOM synchronously and re-anchor in the SAME task, before
        // the browser can paint a frame at the stale scrollTop — that paint is
        // the residual upward flicker. updateComplete then handles the async
        // children that settle over the following frames.
        this.performUpdate();
        anchor();
        void this.updateComplete.then(() => {
            anchor();
            // Re-anchor ONLY when the content height actually changes (async
            // markdown / diff2html / lazy images settling). Anchoring every
            // frame regardless — as a plain rAF loop would — rewrites scrollTop
            // against sub-pixel readback noise and produces a visible jitter.
            // A ResizeObserver fires precisely on the height changes we care
            // about; a timer just bounds how long we keep listening.
            const ro = new ResizeObserver(() => anchor());
            ro.observe(el);
            window.setTimeout(() => {
                ro.disconnect();
                el.style.scrollBehavior = prevBehavior;
            }, 1500);
            appState.loadingOlder = false;
        });
    }

    /** One jump, once Lit has flushed. For the streaming path, which lands here on every delta:
     *  the transcript grows by a few lines at a time and there is nothing to chase, so the
     *  catch-up passes below would triple the work for the same result. */
    private _scrollToBottomNow(): void {
        void this.updateComplete.then(() => this._jumpToBottom('instant'));
    }

    private _jumpToBottom(behavior: ScrollBehavior): void {
        const el = this._messagesEl;
        if (el) {
            el.scrollTo({ top: el.scrollHeight, behavior });
        }
    }

    /**
     * Land at the bottom after content the browser is still sizing.
     *
     * Wait for Lit to flush the DOM before measuring scrollHeight — scrolling in a microtask
     * (pre-render) lands short, leaving the last lines cut off. The two catch-up passes absorb
     * what keeps growing after the first paint: that is images (cv-message awaits fetchChatImage),
     * NOT markdown or diff2html — both are synchronous and already final when Lit is done.
     *
     * `sustainFrames` keeps re-landing for that many frames, for callers that just swapped in a
     * whole page of transcript. Loading it in the caller instead multiplied these three passes by
     * the frame count.
     */
    private _scrollToBottom(behavior: ScrollBehavior = 'smooth', sustainFrames = 0): void {
        void this.updateComplete.then(() => {
            // First pass honours the requested behaviour; the catch-up passes are
            // instant so they don't fight the smooth animation.
            this._jumpToBottom(behavior);
            requestAnimationFrame(() => this._jumpToBottom('instant'));
            setTimeout(() => this._jumpToBottom('instant'), 120);

            let left = sustainFrames;
            const tick = (): void => {
                if (left-- <= 0) {
                    return;
                }
                this._jumpToBottom('instant');
                requestAnimationFrame(tick);
            };
            if (left > 0) {
                requestAnimationFrame(tick);
            }
        });
    }

    private renderEntry(e: UiEntry) {
        return e.kind === 'tool'
            ? this.renderToolRow(e)
            : e.role === 'thinking'
              ? html`<cv-thinking
                    .text=${e.text}
                    ?streaming=${!!e.streaming}
                    .tokens=${e.tokens ?? 0}
                    .durationMs=${e.durationMs ?? 0}
                    .startedAt=${e.startedAt ?? 0}
                    ?redacted=${!!e.redacted}
                ></cv-thinking>`
              : this.renderMessage(e);
    }

    // An exchange = the leading user message(s) then the response (assistant blocks + tool rows).
    // The response and its actions row are wrapped in .cv-response so the row reveals on hovering the
    // RESPONSE only — hovering the user bubble must not light up the response's copy (they're
    // separate turns). Leading user entries render outside that wrapper (each has its own row).
    private renderExchange(group: UiEntry[]) {
        const isUser = (e: UiEntry): boolean => e.kind === 'text' && e.role === 'user';
        let i = 0;
        while (i < group.length && isUser(group[i])) {
            i++;
        }
        const leadUsers = group.slice(0, i);
        const response = group.slice(i);
        return html`<section class="cv-exchange">
            ${leadUsers.map((e) => this.renderEntry(e))}
            ${
                response.length > 0
                    ? html`<div class="cv-response">
                          ${response.map((e) => this.renderEntry(e))}
                          ${this.renderResponseActions(response)}
                      </div>`
                    : nothing
            }
        </section>`;
    }

    // One actions row at the end of a whole exchange's response: copy every finished text block
    // (assistant answers AND slash-command outputs like /config) joined + "x ago" (the last block).
    // Nothing while the last assistant block still streams, or with no copyable text (e.g. a bare
    // tool-only response). A slash-result-only exchange (a command with no assistant reply) still
    // gets the row — its output is worth copying.
    private renderResponseActions(group: UiEntry[]) {
        const blocks = group.filter(
            (e): e is UiAssistantEntry | UiSlashResultEntry =>
                e.kind === 'text' && (e.role === 'assistant' || e.role === 'slash-result'),
        );
        if (blocks.length === 0) {
            return nothing;
        }
        const last = blocks[blocks.length - 1];
        if (last.role === 'assistant' && last.streaming) {
            return nothing;
        }
        const text = blocks.map((b) => b.text).join('\n\n');
        const ts = last.timestamp ?? 0;
        // Keyed on the last text block — the one entry both writers can name: `assistantText` has
        // only the message it just closed, and `exchangeEnded` looks the same one up. Keying on the
        // group's last entry instead would miss a turn that ends on a tool row.
        const metrics = this._turnMetrics.get(last.id) ?? null;
        return renderActionsRow(text, ts, 'Copy', '', /* speak */ true, metrics);
    }

    /** File a finished message's token counts against its entry, for the actions row to show.
     *  Called from both paths that build an assistant entry — the live notification and the
     *  history replay — because the JSONL keeps `usage` on the assistant line and a replayed
     *  turn should still show its tokens. Cost and duration ride `result`, which replay has no
     *  line for, so `exchangeEnded` fills those in later when the turn is live. Sub-agent
     *  messages are skipped: their figures belong to the Agent row, not to the exchange. */
    private _seedTurnTokens(entryId: number, d: AssistantTextNotification | null): void {
        if (d?.parentToolUseId || !d?.usage || !appState.ui.showCostAndDuration) {
            return;
        }
        this._turnMetrics.set(entryId, { costUsd: 0, durationMs: 0, usage: d.usage });
    }

    // The state notice mirrors the connection and dies with it, so it carries no ✕; the error one is
    // a message the user closes when they've read it.
    private _onRemoteControl(n: RemoteControlNotification): void {
        appState.remoteControl = {
            status: n.status as 'disconnected' | 'connecting' | 'connected' | 'error',
            url: n.url ?? undefined,
            detail: n.detail ?? undefined,
        };
        if (n.status === 'connected' && n.url) {
            this._systemNotices?.push({
                key: REMOTE_CONTROL_KEY,
                pinned: true,
                severity: 'info',
                message: `Remote Control is active — continue at <a href="${escapeHtml(n.url)}">claude.ai/code</a>. It ends when this session restarts.`,
            });
        } else {
            this._systemNotices?.dismissByKey(REMOTE_CONTROL_KEY);
        }
        if (n.status === 'error' || (n.status === 'disconnected' && n.detail)) {
            this._systemNotices?.push({
                key: REMOTE_CONTROL_ERROR_KEY,
                severity: 'error',
                // The CLI writes its own reasons ("/login", "disabled by your organization's
                // policy") and they read fine behind a prefix.
                message: n.detail
                    ? `Remote Control error: ${n.detail}`
                    : 'Remote Control failed to start.',
            });
        }
    }

    private renderMessage(e: Exclude<UiEntry, UiToolEntry | UiThinkingEntry>) {
        return html`<cv-message
            .role=${e.role}
            .text=${e.text}
            .trigger=${e.role === 'compact' ? e.trigger : ''}
            .preTokens=${e.role === 'compact' ? e.preTokens : 0}
            .summary=${e.role === 'compact' ? (e.summary ?? '') : ''}
            ?loaded=${e.role === 'compact' ? !!e.loaded : false}
            .uuid=${e.role === 'compact' || e.role === 'user' ? (e.uuid ?? '') : ''}
            ?queued=${e.role === 'user' && !!e.uuid && this._queuedUuids.has(e.uuid)}
            .images=${e.role === 'user' ? (e.images ?? []) : []}
            .files=${e.role === 'user' ? (e.files ?? []) : []}
            ?streaming=${e.role === 'assistant' ? !!e.streaming : false}
            ?isError=${e.role === 'slash-result' ? e.isError : e.role === 'assistant' && !!e.error}
            .timestamp=${
                e.role === 'user' || e.role === 'assistant' || e.role === 'slash-result'
                    ? (e.timestamp ?? 0)
                    : 0
            }
        ></cv-message>`;
    }

    private renderToolRow(e: UiToolEntry) {
        return html`<cv-tool-row
            .data=${e.data}
            .status=${e.status}
            .result=${e.result}
            .elapsedSec=${e.elapsedSec}
            .childItems=${e.children?.items ?? []}
            .fullLineCount=${e.fullLineCount}
            .extras=${e.extras ?? null}
            .agentId=${e.agentId ?? ''}
            .hasMore=${e.children?.hasMore ?? false}
            .showAll=${e.children?.showAll ?? false}
        ></cv-tool-row>`;
    }

    override render() {
        return html`
            <!-- Session/system notices at the top of the chat: a dead CLI process (with View logs),
                 CLI informational advisories (a session model this version no longer knows), … .
                 Turn-scoped ones (rate limit, uploads) live above the composer in cv-prompt — each
                 stack owns its own queue and keeps only its own position. -->
            <cv-notice-stack id="system-notices"></cv-notice-stack>

            <div id="messages" aria-live="polite">
                ${
                    this._transcript.entries.length === 0 && !this._isBusy
                        ? html`<cv-welcome></cv-welcome>`
                        : nothing
                }
                ${this._exchanges.map((group) => this.renderExchange(group))}
                ${
                    this._isBusy && !this._awaitingUser
                        ? html`<cv-spinner .status=${this._status}></cv-spinner>`
                        : nothing
                }
                <!-- Last child of the scroller, stuck to its bottom edge: position:sticky keeps it
                     in view without a wrapper. A wrapper would have been cleaner, but cv-app
                     renders into the light DOM, and re-parenting #messages moves nodes Lit holds
                     markers into — the next update then writes a property onto a node that is
                     gone. -->
                <fluent-button
                    id="jump-to-bottom"
                    shape="circular"
                    size="small"
                    icon-only
                    title="Jump to the latest"
                    ?hidden=${!this._showJump}
                    @click=${(): void => this._scrollToBottom()}
                >
                    ${unsafeHTML(ChevronDown16Regular)}
                </fluent-button>
            </div>

            <div id="composer-area">
                <cv-permission-banner></cv-permission-banner>
                <cv-prompt></cv-prompt>
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-app': CvApp;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { fetchSubagent, fetchContextUsage, fetchCompactSummary } from '../../core/lazy';
import './cv-cli-banner';
import './cv-welcome';
import './cv-prompt';
import './cv-message';
import './cv-copy-btn';
import { formatTimeAgo, formatAbsolute } from '../../core/time';
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
} from '../../core/types';
import { GetHistoryReq } from '../../core/request-types';
import { modelLabel } from '../../core/ai-models';
import { turnErrorLabel } from '../../core/turn-errors';
import { parseLocalCommandOutput } from '../../core/slash-commands';

let _entryIdSeq = 0;

/**
 * Root component. Owns the chat entry list and wires the bridge messages
 * that produce or update entries (`user_text`, `text`, `text_delta`,
 * `tool_use`, `tool_result`, `tool_progress`, `result`, `error`,
 * `clear`, `history`).
 */
@customElement('cv-app')
export class CvApp extends LitElement {
    @state() private _entries: UiEntry[] = [];
    @state() private _exchanges: UiEntry[][] = [];
    @state() private _subagentTasks = new Map<string, SubagentTask>();
    @state() private _isBusy = appState.isBusy;
    @state() private _status = appState.status;
    /** A permission/Ask prompt is awaiting the user → hide the "waiting" spinner
     *  (Claude isn't working, it's waiting for the user to choose). */
    @state() private _awaitingUser = appState.pendingPermission != null;

    @query('#messages') private _messagesEl!: HTMLDivElement;

    private _offs: Array<() => void> = [];
    /**
     * Currently-streaming assistant message, keyed by parent tool_use_id
     * (`''` for root). Lets a sub-agent stream concurrently with the root
     * without mixing the two; delta lookups default to `''`.
     */
    private _streamingMsgs = new Map<string, UiAssistantEntry>();
    /** Currently-streaming thinking block, keyed by parentToolUseId (same nesting rule
     *  as _streamingMsgs). Never persisted — cleared on session clear. */
    private _thinkingMsgs = new Map<string, UiThinkingEntry>();

    override createRenderRoot() {
        return this;
    }

    override connectedCallback(): void {
        super.connectedCallback();
        this._offs.push(appState.on('isBusy', (v) => (this._isBusy = v)));
        this._offs.push(appState.on('status', (v) => (this._status = v)));
        this._offs.push(appState.on('pendingPermission', (v) => (this._awaitingUser = v != null)));
        // Global Esc-to-stop (like VS Code): interrupt generation regardless of
        // focus. Skipped when a permission/Ask prompt is open — there Esc cancels
        // the prompt (the banner handles it, with stopPropagation).
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
                    const streaming = this._streamingMsgs.get(parentId);
                    if (streaming) {
                        streaming.text = data?.text ?? streaming.text;
                        streaming.streaming = false;
                        // The final assistant notification carries the message time; live fallback = now.
                        streaming.timestamp = data?.timestamp ?? Date.now();
                        this._streamingMsgs.delete(parentId);
                        this._entries = [...this._entries];
                    } else {
                        const entry = CvApp.buildAssistantEntry(data);
                        entry.timestamp = data?.timestamp ?? Date.now();
                        this._appendEntry(entry, parentId);
                        queueMicrotask(() => this._scrollToBottom());
                    }
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<AssistantTextDeltaNotification>(
                Msg.toWebView.chat.assistantTextDelta,
                (data) => {
                    const delta = data?.text ?? '';
                    const parentId = data?.parentToolUseId ?? '';
                    let streaming = this._streamingMsgs.get(parentId);
                    if (!streaming) {
                        streaming = this._addText<UiAssistantEntry>(
                            { role: 'assistant', text: delta, streaming: true },
                            parentId,
                        );
                        this._streamingMsgs.set(parentId, streaming);
                    } else {
                        // Auto-follow only if already near the bottom.
                        const atBottom = this._isNearBottom();
                        streaming.text += delta;
                        this._entries = [...this._entries];
                        if (atBottom) {
                            queueMicrotask(() => this._scrollToBottom('instant'));
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
                    let entry = this._thinkingMsgs.get(parentId);
                    if (!entry) {
                        entry = this._addText<UiThinkingEntry>(
                            {
                                role: 'thinking',
                                text: '',
                                streaming: true,
                                tokens: 0,
                                startedAt: Date.now(),
                            },
                            parentId,
                        );
                        this._thinkingMsgs.set(parentId, entry);
                    }
                    if (data?.text) {
                        entry.text += data.text;
                    }
                    // Token: authoritative thinking_tokens (text empty + estimate>=0) SETS and locks;
                    // deltas accumulate until then.
                    if (data && data.estimatedTokens >= 0) {
                        if (!data.text) {
                            entry.tokens = data.estimatedTokens;
                            entry.tokensAuthoritative = true;
                        } else if (!entry.tokensAuthoritative) {
                            entry.tokens = (entry.tokens ?? 0) + data.estimatedTokens;
                        }
                    }
                    this._entries = [...this._entries];
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ThinkingEndedNotification>(
                Msg.toWebView.chat.thinkingEnded,
                (data) => {
                    const parentId = data?.parentToolUseId ?? '';
                    let entry = this._thinkingMsgs.get(parentId);
                    // A redacted_thinking block has no preceding delta → no entry yet: create a static,
                    // text-less one here so it still shows (like VS Code's "✻ Thinking…").
                    if (!entry) {
                        if (!data?.redacted) {
                            return;
                        }
                        entry = this._addText<UiThinkingEntry>(
                            { role: 'thinking', text: '', redacted: true },
                            parentId,
                        );
                    }
                    entry.streaming = false;
                    entry.durationMs = entry.startedAt ? Date.now() - entry.startedAt : 0;
                    this._thinkingMsgs.delete(parentId);
                    this._entries = [...this._entries];
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
                    // the user is left guessing. (VS Code only uses this text to improve a crash
                    // message; in a chat surface a notice is the useful form.)
                    if (data?.isError) {
                        const label = turnErrorLabel(data.errorKind ?? '');
                        const detail = (data.errorText ?? '').trim();
                        this._addText<UiSlashResultEntry>({
                            role: 'slash-result',
                            text: detail ? `${label} — ${detail}` : label,
                            isError: true,
                        });
                    }
                    if (appState.ui.showCostAndDuration && data?.durationMs != null) {
                        // Field is `costUsd` to match the host payload (not `totalCost`).
                        const cost = data.costUsd ? `$${data.costUsd.toFixed(4)}` : '';
                        const dur = `${(data.durationMs / 1000).toFixed(1)}s`;
                        this._addText({
                            role: 'result',
                            text: [cost, dur].filter(Boolean).join(' · '),
                        });
                    }
                    if (this._streamingMsgs.size > 0) {
                        for (const m of this._streamingMsgs.values()) {
                            m.streaming = false;
                        }
                        this._streamingMsgs.clear();
                        this._entries = [...this._entries];
                    }
                },
            ),
        );

        // subagent_loaded: full sub-agent transcript arrives (expand "show all").
        // Replace the kept ≤3 with all children; hasMore clears (nothing hidden).
        // Collapse re-slices to 3 — no separate cache is kept (lazy: re-fetch on re-expand).
        // (The former subagent_loaded listener moved into the fetchSubagent().then in
        // _toggleSubagentExpand — the correlated response drives the upsert now.)

        this._offs.push(
            bridge.onNotification(Msg.toWebView.chat.cleared, () => {
                // Abort in-flight requests for the old session so their Promises reject
                // instead of resolving against the new session (or hanging until timeout).
                bridge.rejectAllPending('session changed');
                this._entries = [];
                this._exchanges = [];
                this._streamingMsgs.clear();
                this._thinkingMsgs.clear();
                appState.currentSessionId = null;
                appState.oldestLoadedOffset = -1;
                appState.hasMoreHistory = false;
                appState.loadingOlder = false;
                appState.contextUsage = null;
            }),
        );

        this._offs.push(
            bridge.onNotification<CompactedNotification>(Msg.toWebView.chat.compacted, (data) => {
                // Header-only: one notification, no summary. The summary text is fetched
                // lazily on first expand (compact-expand listener below) and cached.
                this._addText<UiCompactEntry>({
                    role: 'compact',
                    text: '',
                    uuid: data?.uuid ?? '',
                    trigger: data?.trigger ?? 'auto',
                    preTokens: data?.preTokens ?? 0,
                    loaded: false,
                });
            }),
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
                    const existing = this._findTool(data.id);
                    if (existing) {
                        existing.data = {
                            id: data.id,
                            name: data.name,
                            input: (data.input ?? {}) as Record<string, unknown>,
                        };
                        this._entries = [...this._entries];
                        return;
                    }
                    this._appendEntry(
                        CvApp.buildToolEntry(data),
                        data.parentToolUseId ?? undefined,
                    );
                    queueMicrotask(() => this._scrollToBottom());
                },
            ),
        );

        this._offs.push(
            bridge.onNotification<ToolResultNotification>(Msg.toWebView.chat.toolResult, (data) => {
                if (!data?.toolUseId) {
                    return;
                }
                const tool = this._findTool(data.toolUseId);
                if (!tool) {
                    return;
                }
                const atBottom = this._isNearBottom();
                CvApp.applyToolResult(tool, data);
                this._entries = [...this._entries];
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
                    const tool = this._findTool(data.toolUseId);
                    if (!tool) {
                        return;
                    }
                    // Wire field is elapsedSeconds (from the host); the entry keeps it as
                    // elapsedSec (internal UI state).
                    tool.elapsedSec = data.elapsedSeconds ?? 0;
                    this._entries = [...this._entries];
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
                    this._entries = out;
                    this._rebuildExchanges();
                    this._streamingMsgs.clear();
                    // Land at the bottom. Re-jump for several frames because async-rendered
                    // children (markdown, diff2html, lazy images) keep growing scrollHeight.
                    void this.updateComplete.then(() => {
                        let frames = 0;
                        const tick = (): void => {
                            this._scrollToBottom('instant');
                            if (++frames < 10) {
                                requestAnimationFrame(tick);
                            }
                        };
                        requestAnimationFrame(tick);
                    });
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
                        taskType: d.taskType,
                        toolUseId: d.toolUseId ?? undefined,
                        recentTools: [],
                        usage: { totalTokens: 0, toolUses: 0, durationMs: 0 },
                    });
                    this._subagentTasks = m;
                    appState.subagentTasks = [...m.values()];
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
                    appState.subagentTasks = [...m.values()];
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
                    const m = new Map(this._subagentTasks);
                    m.delete(d.taskId);
                    this._subagentTasks = m;
                    appState.subagentTasks = [...m.values()];
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
        window.removeEventListener('keydown', this._onGlobalEsc);
        this.removeEventListener('subagent-toggle', this._onChildrenToggle as EventListener);
        this.removeEventListener('compact-expand', this._onCompactExpand as EventListener);
        this.removeEventListener('model-switched', this._onModelSwitched as EventListener);
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
        bridge.sendNotification(Msg.fromWebView.cli.stop, {});
        appState.isBusy = false;
    };

    /**
     * Convert a Chat.History `messages[]` array into the backing UiEntry
     * list. Shared by the initial load and lazy-loaded older pages.
     */
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
                        place(CvApp.buildAssistantEntry(d), d.parentToolUseId);
                    }
                    break;
                }
                case Msg.toWebView.chat.toolPermission: {
                    const d = ev.data as ToolPermissionNotification;
                    if (d.id && d.name) {
                        place(CvApp.buildToolEntry(d), d.parentToolUseId);
                    }
                    break;
                }
                case Msg.toWebView.chat.toolResult: {
                    const d = ev.data as ToolResultNotification;
                    const hit = d.toolUseId ? findTool(d.toolUseId) : null;
                    if (hit) {
                        CvApp.applyToolResult(hit, d);
                    }
                    break;
                }
                case Msg.toWebView.chat.compacted: {
                    // Header-only, same shape as the live notification — the summary is
                    // fetched lazily on first expand (compact-expand listener), cached after.
                    const d = ev.data as CompactedNotification;
                    const compactEntry: UiCompactEntry = {
                        kind: 'text',
                        id: ++_entryIdSeq,
                        role: 'compact',
                        text: '',
                        uuid: d?.uuid ?? '',
                        trigger: d?.trigger ?? 'auto',
                        preTokens: d?.preTokens ?? 0,
                        loaded: false,
                    };
                    place(compactEntry);
                    break;
                }
                default:
                    break;
            }
        }

        // A tool_use with no matching tool_result on disk was never completed — the session
        // ended while it was open (e.g. an AskUserQuestion the user closed without answering).
        // In replay nothing more is coming, so mark it interrupted (static red dot, like the
        // VS Code extension) instead of leaving it 'pending' (a spinning "in progress" dot).
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
                    // Child tool rows inherit the Agent's agentId so the open-output path
                    // routes to the sub-agent transcript file (agent-<agentId>.jsonl).
                    if (e.agentId) {
                        for (const c of children) {
                            if (c.kind === 'tool') {
                                c.agentId = e.agentId;
                            }
                        }
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
    private _addText<E extends Extract<UiEntry, { kind: 'text' }>>(
        msg: Omit<E, 'kind' | 'id'>,
        parentId = '',
    ): E {
        const entry = { kind: 'text', id: ++_entryIdSeq, ...msg } as E;
        this._appendEntry(entry, parentId);
        queueMicrotask(() => this._scrollToBottom());
        return entry;
    }

    /**
     * Append `entry` under the tool row `parentId`, or to the root list
     * when no/unknown parent. Arrays are replaced (not mutated) for Lit.
     *
     * Memoria-minima: when appending a child under an Agent parent, keep only
     * the last 3 children — a ring of 3 (`slice(-3)`). The `hasMore` flag flips
     * true once a 4th child arrives (children.items already holds 3 before the push):
     * the "…" indicator only signals "more exist", not the count, so a boolean
     * is enough — no running counter needed.
     */
    private _appendEntry(entry: UiEntry, parentId?: string): void {
        if (parentId) {
            const parent = this._findTool(parentId);
            if (parent) {
                // The sub-agent's first message echoes the prompt the Agent tool was
                // launched with — it's already shown as the Agent row's IN, so drop the
                // duplicate (matches the VS Code extension, which renders prompt in IN only).
                if (
                    entry.kind === 'text' &&
                    entry.role === 'user' &&
                    entry.text?.trim() === String(parent.data?.input?.prompt ?? '').trim()
                ) {
                    return;
                }
                // Inherit agentId from the parent Agent entry to child tool rows,
                // so the open-output path can route to the sub-agent transcript.
                if (parent.agentId && entry.kind === 'tool') {
                    (entry as UiToolEntry).agentId = parent.agentId;
                }
                // First child under this parent creates the children block.
                const kids = (parent.children ??= { items: [], hasMore: false, showAll: false });
                if (kids.showAll) {
                    // Show-all: keep the full list, upsert (a re-emitted tool row updates
                    // in place — e.g. pending → done — instead of duplicating).
                    kids.items = this._upsertChild(kids.items, entry);
                } else {
                    // Collapsed: ring of 3. A 4th child means more exist beyond the window.
                    if (kids.items.length >= 3) {
                        kids.hasMore = true;
                    }
                    kids.items = [...kids.items, entry].slice(-3);
                }
                this._entries = [...this._entries];
                return;
            }
        }
        this._entries = [...this._entries, entry];
        this._rebuildExchanges();
    }

    /** Expand/collapse a nested Agent box (CustomEvent from cv-tool-row). */
    private _onChildrenToggle = (
        e: CustomEvent<{ agentId: string; expand: boolean; preview?: boolean }>,
    ): void => {
        const { agentId, expand, preview } = e.detail ?? {};
        const parent = agentId ? this._findToolByAgentId(agentId) : null;
        if (!parent) {
            return;
        }
        // Ensure the children block exists (a history Agent may not have fetched anything yet).
        const kids = (parent.children ??= { items: [], hasMore: false, showAll: false });
        if (expand) {
            // Rule: history FETCHES, live SHOWS what it already has in memory.
            //  - preview (first chevron expand): fetch only if children are empty (history). Live
            //    already streamed them in → no fetch.
            //  - Show all (preview=false): mark showAll; fetch the whole transcript only if there's
            //    more than we hold (hasMore = a history preview). Live/already-full → just show all.
            const showAll = !preview;
            // Show all sets the flag; preview (chevron open) leaves it false → renderChildren shows ≤3.
            // Row open/closed is the component's own `_expanded`, not tracked here.
            kids.showAll = showAll;

            const needFetch = preview
                ? kids.items.length === 0 // history preview
                : kids.hasMore; // Show all with more on disk than we hold
            if (!needFetch) {
                this._entries = [...this._entries];
                return;
            }
            // Show all replaces with the whole transcript, so clear first (it re-adds in order).
            if (showAll) {
                kids.items = [];
            }
            fetchSubagent(agentId, { preview: !!preview })
                .then((data) => {
                    const p = this._findToolByAgentId(data.agentId);
                    if (!p) {
                        return;
                    }
                    const pk = (p.children ??= { items: [], hasMore: false, showAll: false });
                    const full = this._replayEvents(data.events ?? []);
                    // The transcript's first user message echoes the launch prompt (already shown as
                    // the Agent row's IN). Its events carry no parentToolUseId, so the _replayEvents
                    // post-pass filter doesn't reach them — drop the echo here.
                    const prompt = String(p.data?.input?.prompt ?? '').trim();
                    let list = pk.items;
                    for (const child of full) {
                        if (
                            prompt &&
                            child.kind === 'text' &&
                            child.role === 'user' &&
                            child.text?.trim() === prompt
                        ) {
                            continue;
                        }
                        list = this._upsertChild(list, child);
                    }
                    // Preview keeps the last 3 (and flags "…" if more); full keeps everything and is
                    // now complete in memory (hasMore=false → no more fetches).
                    pk.items = preview ? list.slice(-3) : list;
                    pk.hasMore = preview ? list.length > 3 : false;
                    pk.showAll = !preview;
                    this._entries = [...this._entries];
                })
                .catch(() => {
                    /* timeout / not found — leave the kept children as-is */
                });
        } else {
            // "Reduce" (Show all → off): show the last 3 again, but KEEP the full list in memory so a
            // later Show all doesn't refetch (and live never can). renderChildren slices the view.
            // hasMore reflects that more is held than shown.
            kids.showAll = false;
            kids.hasMore = kids.items.length > 3;
        }
        this._entries = [...this._entries];
    };

    /** A compact separator's <details> opened for the first time: fetch the summary lazily
     *  and cache it (`loaded` gates a refetch on collapse/re-expand). */
    private _onCompactExpand = (e: CustomEvent<{ uuid: string }>): void => {
        const { uuid } = e.detail ?? {};
        if (!uuid) {
            return;
        }
        const entry = this._entries.find(
            (en): en is UiCompactEntry =>
                en.kind === 'text' && en.role === 'compact' && en.uuid === uuid,
        );
        if (!entry || entry.loaded) {
            return;
        }
        fetchCompactSummary(appState.currentSessionId, uuid)
            .then((res) => {
                entry.summary = res.summary;
                entry.loaded = true;
                this._entries = [...this._entries];
            })
            .catch(() => {
                /* timeout / not found — leave "Loading…" as-is */
            });
    };

    /** User picked a model from the menu (cv-prompt): show "Switched to X" — but only
     *  during a live chat, not on an empty transcript (nothing above it would be noise). */
    private _onModelSwitched = (e: CustomEvent<{ value: string }>): void => {
        const value = e.detail?.value;
        if (!value || this._entries.length === 0) {
            return;
        }
        this._addText({ role: 'status', text: `Switched to ${modelLabel(value)}` });
    };

    /** Stable identity of a sub-agent child: toolUseId for tools, uuid for text. */
    private static _childKey(e: UiEntry): string {
        if (e.kind === 'tool') {
            return e.toolUseId;
        }
        return ('uuid' in e ? e.uuid : undefined) ?? `t${e.id}`;
    }

    // ---- Pure DTO → UiEntry builders, shared by the live handlers (then append)
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
        };
    }

    /** ToolPermissionNotification → a pending tool row. */
    private static buildToolEntry(d: ToolPermissionNotification): UiToolEntry {
        const input = (d.input ?? {}) as Record<string, unknown>;
        return {
            kind: 'tool',
            id: ++_entryIdSeq,
            toolUseId: d.id,
            data: { id: d.id, name: d.name, input },
            status: 'pending',
            result: '',
            fullLineCount: 0,
            elapsedSec: 0,
        };
    }

    /** Apply a ToolResultNotification onto an already-built tool row (in place). */
    private static applyToolResult(e: UiToolEntry, d: ToolResultNotification): void {
        e.status = d.isError ? 'error' : 'done';
        e.result = d.result ?? '';
        e.fullLineCount = d.fullLineCount ?? 0;
        if (d.agentId) {
            e.agentId = d.agentId;
        }
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

    /** Find a tool entry by toolUseId, walking nested children. */
    private _findTool(toolUseId: string): UiToolEntry | null {
        const visit = (list: UiEntry[]): UiToolEntry | null => {
            for (const e of list) {
                if (e.kind !== 'tool') {
                    continue;
                }
                if (e.toolUseId === toolUseId) {
                    return e;
                }
                if (e.children?.items.length) {
                    const hit = visit(e.children.items);
                    if (hit) {
                        return hit;
                    }
                }
            }
            return null;
        };
        return visit(this._entries);
    }

    /** Locate an Agent tool entry by its sub-agent id (set on the Agent record). */
    private _findToolByAgentId(agentId: string): UiToolEntry | null {
        const visit = (list: UiEntry[]): UiToolEntry | null => {
            for (const e of list) {
                if (e.kind !== 'tool') {
                    continue;
                }
                if (e.agentId === agentId) {
                    return e;
                }
                if (e.children?.items.length) {
                    const hit = visit(e.children.items);
                    if (hit) {
                        return hit;
                    }
                }
            }
            return null;
        };
        return visit(this._entries);
    }

    /** Rebuilds _exchanges from _entries. Each user message opens a new
     *  exchange; entries before the first user (history page boundary) go
     *  in their own leading group. Called after structural changes only —
     *  in-place mutations (streaming text, tool status) skip this. */
    private _rebuildExchanges(): void {
        const groups: UiEntry[][] = [];
        let current: UiEntry[] = [];
        for (const e of this._entries) {
            if (e.kind === 'text' && e.role === 'user') {
                if (current.length) {
                    groups.push(current);
                }
                current = [e];
            } else {
                current.push(e);
            }
        }
        if (current.length) {
            groups.push(current);
        }
        this._exchanges = groups;
    }

    /** True when scrolled at/near the bottom. Gates stream auto-follow so
     *  the user isn't yanked down while reading scrolled-up content. */
    private _isNearBottom(threshold = 80): boolean {
        const el = this._messagesEl;
        if (!el) {
            return true;
        }
        return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
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

    private _prependWithAnchor(older: UiEntry[]): void {
        const el = this._messagesEl;
        if (!el) {
            this._entries = [...older, ...this._entries];
            this._rebuildExchanges();
            appState.loadingOlder = false;
            return;
        }
        const distFromBottom = el.scrollHeight - el.scrollTop;
        const prevBehavior = el.style.scrollBehavior;
        el.style.scrollBehavior = 'auto';

        const anchor = (): void => {
            el.scrollTop = el.scrollHeight - distFromBottom;
        };

        this._entries = [...older, ...this._entries];
        this._rebuildExchanges();
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

    private _scrollToBottom(behavior: ScrollBehavior = 'smooth'): void {
        // Wait for Lit to flush the DOM before measuring scrollHeight — scrolling
        // in a microtask (pre-render) lands short, leaving the last lines cut off.
        // A second pass after a frame absorbs async layout (markdown, images,
        // highlight) that keeps growing scrollHeight just after the first paint.
        const jump = (b: ScrollBehavior) => {
            const el = this._messagesEl;
            if (el) {
                el.scrollTo({ top: el.scrollHeight, behavior: b });
            }
        };
        void this.updateComplete.then(() => {
            // First pass honours the requested behaviour; the catch-up passes are
            // instant so they don't fight the smooth animation.
            jump(behavior);
            requestAnimationFrame(() => jump('instant'));
            setTimeout(() => jump('instant'), 120);
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

    // One actions row at the end of a whole response: copy the whole answer (every finished assistant
    // block joined) + "x ago" (the last block, i.e. when the response finished). Just two elements, so
    // inline here — no component. Nothing while the last block still streams, or with no assistant text.
    private renderResponseActions(group: UiEntry[]) {
        const blocks = group.filter(
            (e): e is UiAssistantEntry => e.kind === 'text' && e.role === 'assistant',
        );
        if (blocks.length === 0 || blocks[blocks.length - 1].streaming) {
            return nothing;
        }
        const text = blocks.map((b) => b.text).join('\n\n');
        const ts = blocks[blocks.length - 1].timestamp ?? 0;
        return html`<div class="cv-response-actions">
            <cv-copy-btn .text=${text} title="Copy response"></cv-copy-btn>
            ${
                ts > 0
                    ? html`<span class="cv-ts" title=${formatAbsolute(ts)}
                          >${formatTimeAgo(ts)}</span
                      >`
                    : nothing
            }
        </div>`;
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
            .images=${e.role === 'user' ? (e.images ?? []) : []}
            .files=${e.role === 'user' ? (e.files ?? []) : []}
            ?streaming=${e.role === 'assistant' ? !!e.streaming : false}
            ?isError=${e.role === 'slash-result' ? e.isError : false}
            .timestamp=${e.role === 'user' || e.role === 'assistant' ? (e.timestamp ?? 0) : 0}
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
            .agentId=${e.agentId ?? ''}
            .hasMore=${e.children?.hasMore ?? false}
            .showAll=${e.children?.showAll ?? false}
        ></cv-tool-row>`;
    }

    override render() {
        return html`
            <cv-cli-banner></cv-cli-banner>

            <div id="messages" aria-live="polite">
                ${
                    this._entries.length === 0 && !this._isBusy
                        ? html`<cv-welcome></cv-welcome>`
                        : nothing
                }
                ${this._exchanges.map((group) => this.renderExchange(group))}
                ${
                    this._isBusy && this._streamingMsgs.size === 0 && !this._awaitingUser
                        ? html`<cv-spinner .status=${this._status}></cv-spinner>`
                        : nothing
                }
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

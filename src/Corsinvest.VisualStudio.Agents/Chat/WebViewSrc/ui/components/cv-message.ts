/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing, type PropertyValues } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import BranchFork16Regular from '@fluentui/svg-icons/icons/branch_fork_16_regular.svg';
import './cv-copy-btn';
import './cv-attach-chip';
import { renderMarkdown, renderMarkdownStreaming } from '../../core/markdown';
import { stableMarkdownSplit } from '../../core/markdown-split';
import { escapeHtml } from '../../core/html';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { fetchChatImage, openChatDocument } from '../../core/lazy';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import { iconUrl } from '../../core/icon-url';
import { fileName } from '../../core/path';
import { formatTokenCount } from '../helpers/format';
import { displayPathUi } from '../paths';
import { cleanMessageOnlyText } from '../../core/ide';
import { renderSlashCommand } from '../../core/slash-commands';
import { openLightbox } from '../../core/dialog-host';
import { observeSize } from '../resize';
import { EMPTY } from '../../core/types';
import type {
    IdeContextRef,
    ForkNotification,
    IdeFileNotification,
    ExternalUrlNotification,
    UiImage,
    UiFile,
    MessageRole,
} from '../../core/types';

/**
 * Single chat bubble. Role picks the variant; `text` is markdown for
 * assistant/result, plaintext elsewhere. `streaming` shows verbatim text
 * while growing, then re-renders as markdown when the parent clears it.
 */
@customElement('cv-message')
export class CvMessage extends LitElement {
    // reflect: the sticky-user CSS keys off [msg-role="user"] to pin only real user bubbles,
    // never a leading assistant/tool group that a history page split off from its user.
    // NOT `role`: that is the ARIA attribute, and 'status' is a real ARIA role — reflecting it
    // made every status bubble a live region nested inside cv-app's aria-live #messages.
    @property({ reflect: true, attribute: 'msg-role' }) role: MessageRole = 'assistant';
    // Typed during a turn and still waiting for it to end: reflected so chat.css can fade the
    // bubble, since an unsent message that looks sent is the whole reason this exists.
    @property({ type: Boolean, reflect: true }) queued = false;
    @property() text = '';
    // role:'compact' only — header fields (trigger/tokens) + the lazily-fetched summary,
    // shown in the expandable <details> body. `loaded` gates the fetch (cached after).
    @property() trigger = '';
    @property({ type: Number }) preTokens = 0;
    @property() summary = '';
    @property({ type: Boolean }) loaded = false;
    @property() uuid = '';
    @property({ type: Boolean }) streaming = false;
    // Message time (epoch ms) for the actions row's "x ago"; 0 = none (hide it).
    @property({ type: Number }) timestamp = 0;
    // role:'slash-result' only — true for <local-command-stderr> (rendered red).
    @property({ type: Boolean }) isError = false;
    @property({ attribute: false }) images: UiImage[] = [];
    @property({ attribute: false }) files: UiFile[] = [];
    /** Editor context for this turn, extracted by buildUserEntry — `text` never carries the
     *  `<ide_*>` block any more. */
    @property({ attribute: false }) ideRefs: IdeContextRef[] = [];

    @property({ type: Boolean, reflect: true }) expanded = false;
    @state() private _isOverflowing = false;
    // Re-measure the truncation on width changes: text re-wraps, so the px cap and the "Show more"
    // decision must be recomputed. updated() alone fires on property change, not on resize.
    private _unobserve?: () => void;
    private readonly _subs = new StateSubscriptions(this);
    // Last observed width — the observer fires on our own max-height writes too, so re-measure only
    // when the WIDTH actually changed (that's what re-wraps the text). Avoids a feedback loop.
    private _lastWidth = 0;

    // Streaming markdown throttle: re-running the full marked→hljs→DOMPurify
    // pipeline on every token janks long answers, so cache the HTML and refresh
    // it at most every STREAM_MD_MS. A trailing timer guarantees the last chunk
    // renders even if it lands inside the throttle window.
    private static readonly STREAM_MD_MS = 75;
    private _streamHtml = '';
    private _streamText = '';
    private _streamAt = 0;
    private _streamTimer?: ReturnType<typeof setTimeout>;
    // Markdown before the last blank line: settled, so it is rendered once and kept. Only the tail
    // after it is re-rendered per pass, which is what stops the cost growing with the answer.
    private _stableHtml = '';
    private _stableUpTo = 0;
    // The exact text the stable HTML was built from: if the incoming text stops starting with it,
    // this is not the same message growing and the prefix has to go.
    private _stablePrefix = '';

    /**
     * Render the growing text without re-parsing what is already settled.
     *
     * Markdown before the last blank line cannot be changed by what comes after, so it is parsed
     * once and appended to `_stableHtml`; each pass then only parses the tail. Without this the
     * cost climbs with the answer — measured 2.5ms per pass over the first third of a 22k-char
     * reply against 8.2ms over the last.
     *
     * The stable prefix is dropped whenever the text stops extending what we saw (a re-render from
     * history, or the text being replaced), so a stale prefix can never survive into a new message.
     */
    private _renderIncremental(): string {
        if (!this.text.startsWith(this._stablePrefix)) {
            this._stableHtml = '';
            this._stableUpTo = 0;
            this._stablePrefix = '';
        }
        const cut = stableMarkdownSplit(this.text);
        if (cut > this._stableUpTo) {
            this._stableHtml += renderMarkdown(this.text.slice(this._stableUpTo, cut));
            this._stableUpTo = cut;
            this._stablePrefix = this.text.slice(0, cut);
        }
        return this._stableUpTo === 0
            ? renderMarkdownStreaming(this.text)
            : this._stableHtml + renderMarkdownStreaming(this.text.slice(this._stableUpTo));
    }

    /** Throttled streaming markdown: returns cached HTML unless enough time has
     *  passed (or the text shrank/reset), scheduling a trailing refresh so the
     *  final partial chunk isn't stuck behind the throttle. */
    private _streamingMarkdown(now: number): string {
        if (this.text === this._streamText) {
            return this._streamHtml;
        }
        const due = now - this._streamAt >= CvMessage.STREAM_MD_MS;
        if (due || this._streamHtml === '') {
            this._streamHtml = this._renderIncremental();
            this._streamText = this.text;
            this._streamAt = now;
            if (this._streamTimer) {
                clearTimeout(this._streamTimer);
                this._streamTimer = undefined;
            }
        } else if (!this._streamTimer) {
            // Not due yet: render the stale HTML now, but schedule a refresh so
            // the newest text shows once the window elapses.
            this._streamTimer = setTimeout(() => {
                this._streamTimer = undefined;
                this.requestUpdate();
            }, CvMessage.STREAM_MD_MS);
        }
        return this._streamHtml;
    }

    override createRenderRoot() {
        return this;
    }

    constructor() {
        super();
        // previewLines IS the cap, so a change to it changes the truncation of every bubble.
        this._subs.on('ui', () => this._measure());
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        if (this._streamTimer) {
            clearTimeout(this._streamTimer);
            this._streamTimer = undefined;
        }
        this._unobserve?.();
        this._unobserve = undefined;
    }

    override updated(changed: PropertyValues): void {
        // Only what can change the measured height. _measure() writes maxHeight and then reads
        // scrollHeight, which is a synchronous layout: doing it on every update meant one forced
        // reflow per user bubble per pass. Width changes come from the ResizeObserver below, and
        // previewLines — the cap itself, which lives in the options and not in a property — from
        // the state subscription in connectedCallback.
        if (changed.has('text') || changed.has('expanded') || changed.has('role')) {
            this._measure();
        }
        // Observe width once the row exists: re-wrapping on resize changes the truncation.
        if (this.role === 'user' && !this._unobserve) {
            const el = this.querySelector('.cv-message.user') as HTMLElement | null;
            if (el) {
                this._lastWidth = el.clientWidth;
                this._unobserve = observeSize(el, (entry) => {
                    const w = entry.contentRect.width;
                    if (Math.abs(w - this._lastWidth) < 1) {
                        return; // height-only change (our own max-height write) — skip
                    }
                    this._lastWidth = w;
                    this._measure();
                });
            }
        }
    }

    /** Cap the message TEXT to previewLines and flag overflow (drives the fade + "Show more").
     *  The cap goes on .md (plain block, not the flex body/bubble) so it clips cleanly with no
     *  scrollbar, and the "Show more" button below it stays inside the bubble, visible. */
    private _measure(): void {
        if (this.role !== 'user') {
            return;
        }
        const el = this.querySelector('.cv-message.user') as HTMLElement | null;
        const md = this.querySelector('.cv-msg-body .md') as HTMLElement | null;
        if (!el || !md) {
            return;
        }
        // Measure natural scrollHeight with the cap removed, then re-apply the cap
        // (lineHeight * previewLines) only when collapsed.
        md.style.maxHeight = '';
        const cs = getComputedStyle(md);
        const lineHeight = parseFloat(cs.lineHeight) || 20;
        const lines = appState.ui.previewLines || 3;
        const threshold = lineHeight * lines;
        const naturalHeight = md.scrollHeight;
        // +4 tolerance absorbs sub-pixel lineHeight rounding.
        const overflows = naturalHeight > threshold + 4;
        // Drives the clip + fade CSS; only when truncated, so short bubbles keep their descenders.
        el.classList.toggle('is-overflowing', overflows && !this.expanded);
        if (overflows && !this.expanded) {
            md.style.maxHeight = `${threshold}px`;
        }
        if (overflows !== this._isOverflowing) {
            this._isOverflowing = overflows;
        }
    }

    /**
     * Bottom hover actions row (user messages): Copy + Fork (only with a uuid, i.e. replayed from
     * JSONL history — live messages have none) + "x ago" timestamp. Inline — cv-copy-btn is the
     * shared icon button; Fork is a plain .trigger (styled in chat.css). Expand is NOT here: long
     * messages get an always-visible "Show more" button on the fade instead (see the user render).
     */
    private _renderActions() {
        const copyText = cleanMessageOnlyText(this.text);
        const showFork = this.role === 'user' && !!this.uuid;
        return html`<div class="cv-msg-actions">
            <cv-copy-btn .text=${copyText} title="Copy message"></cv-copy-btn>
            ${
                showFork
                    ? html`<fluent-button
                          class="trigger"
                          appearance="subtle"
                          shape="rounded"
                          size="small"
                          icon-only
                          title="Fork conversation from here"
                          @click=${this._onFork}
                      >
                          ${unsafeHTML(BranchFork16Regular)}
                      </fluent-button>`
                    : nothing
            }
            ${
                this.timestamp > 0
                    ? html`<cv-time-ago .ms=${this.timestamp}></cv-time-ago>`
                    : nothing
            }
        </div>`;
    }

    private _onFork = (e: Event): void => {
        e.stopPropagation();
        // Drop focus so the actions row (shown on :focus-within) doesn't stay after the pointer leaves.
        (e.currentTarget as HTMLElement | null)?.blur();
        if (!this.uuid) {
            return;
        }
        bridge.sendNotification<ForkNotification>(Msg.fromWebView.session.fork, {
            messageUuid: this.uuid,
        });
    };

    // Delegated click on the rendered markdown: a "path:line" file link (added by the fileLink
    // marked extension). Route by kind: a standalone document (an .html/.htm report, or anything the
    // model wrote with a file:// scheme) opens in the default browser via the shell — you want it
    // rendered, not its source. Everything else (code/text) opens in VS at the line; the host
    // resolves the path (absolute/relative to the workdir, else searched by name in the workspace).
    private _onMdClick = (e: Event): void => {
        const a = (e.target as HTMLElement | null)?.closest('a.cv-file-link') as HTMLElement | null;
        if (!a) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        const filePath = a.getAttribute('data-file') ?? '';
        const line = Number(a.getAttribute('data-line') ?? 0) || 0;
        // Last line of a "100-120" range, so the host selects the block instead of just placing the
        // caret; equal to `line` for a plain reference.
        const lineEnd = Number(a.getAttribute('data-line-end') ?? 0) || line;
        if (!filePath) {
            return;
        }
        const isDocument = /^file:\/\//i.test(filePath) || /\.html?($|[?#])/i.test(filePath);
        if (isDocument) {
            // Normalize backslashes to a proper file:// URL the shell opens in the browser.
            const url = /^file:\/\//i.test(filePath)
                ? filePath
                : 'file:///' + filePath.replace(/\\/g, '/').replace(/^\/+/, '');
            bridge.sendNotification<ExternalUrlNotification>(Msg.fromWebView.open.externalUrl, {
                url,
            });
            return;
        }
        bridge.sendNotification<IdeFileNotification>(Msg.fromWebView.open.ideFile, {
            filePath,
            startLine: line,
            endLine: lineEnd,
        });
    };

    private _onToggleExpand = (e: Event): void => {
        e.stopPropagation();
        const wasExpanded = this.expanded;
        this.expanded = !this.expanded;
        // On expand, scroll to the top ONLY if the now-taller message doesn't
        // fully fit in the viewport. If it's already fully visible, don't move
        // the view — scrolling when unneeded is jarring.
        if (!wasExpanded) {
            void this.updateComplete.then(() => {
                const rect = this.getBoundingClientRect();
                const overflowsBottom = rect.bottom > window.innerHeight;
                if (overflowsBottom) {
                    this.scrollIntoView({ block: 'start', behavior: 'smooth' });
                }
            });
        }
    };

    /** Image attachment chip: extension-typed icon + name. Click opens
     *  the lightbox (live: dataUrl is local; history: ask the host to
     *  fetch the bytes from the JSONL via open_history_block). */
    private _renderImageChips(images: UiImage[]) {
        return images.map(
            (img) =>
                html`<cv-attach-chip
                    .src=${img.preview ?? iconUrl(img.name)}
                    .label=${img.name}
                    title=${img.name}
                    @click=${() => this._onImageClick(img)}
                ></cv-attach-chip>`,
        );
    }

    /** File attachment chip: extension-typed icon + filename. Click opens
     *  the file in VS (live) or fetches the document from history. */
    private _renderFileChips(files: UiFile[]) {
        return files.map(
            (f) =>
                html`<cv-attach-chip
                    .src=${iconUrl(f.name)}
                    .label=${f.name}
                    title=${f.name}
                    @click=${() => this._onFileClick(f)}
                ></cv-attach-chip>`,
        );
    }

    private async _onImageClick(img: UiImage): Promise<void> {
        if (img.lazy) {
            try {
                const blk = await fetchChatImage(img.lazy.uuid, img.lazy.blockIdx);
                if (blk?.base64) {
                    openLightbox({
                        src: `data:${blk.mediaType ?? 'image/png'};base64,${blk.base64}`,
                        name: img.name,
                    });
                }
            } catch {
                // Timeout or block-not-found: nothing to show, leave the placeholder.
            }
            return;
        }
        if (img.dataUrl) {
            openLightbox({ src: img.dataUrl, name: img.name });
        }
    }

    private _onFileClick(f: UiFile): void {
        // File chips only ever carry lazy coords (a stripped history document); attachments
        // never have a file path, so the click just fetches the document.
        if (f.lazy) {
            openChatDocument(f.lazy.uuid, f.lazy.blockIdx);
        }
    }

    /** Render one chip per IDE context ref attached to a user message. */
    private _renderIdeChips(refs: readonly IdeContextRef[]) {
        return refs.map((r) => {
            // Chip shows `name:start-end` (editor style, range only for a real
            // selection); tooltip carries the full relative path.
            const rel = displayPathUi(r.filePath);
            const name = fileName(r.filePath);
            const range = r.startLine ? `:${r.startLine}-${r.endLine}` : '';
            return html`<cv-attach-chip
                accent="brand"
                .src=${iconUrl(name)}
                .label=${`${name}${range}`}
                title=${rel || r.filePath}
                @click=${() =>
                    bridge.sendNotification<IdeFileNotification>(Msg.fromWebView.open.ideFile, {
                        filePath: r.filePath,
                        startLine: r.startLine ?? 0,
                        endLine: r.endLine ?? r.startLine ?? 0,
                    })}
            ></cv-attach-chip>`;
        });
    }

    override render() {
        switch (this.role) {
            case 'slash-result':
                // A slash command's own output (<local-command-stdout>/stderr>, already parsed into
                // `text` by buildUserEntry) — a preformatted monospace block, not a user bubble. No
                // per-message copy: the exchange's single end-of-response actions row copies it too.
                return this.text
                    ? html`<div class="cv-message slash-result${this.isError ? ' error' : ''}">
                          <pre>${this.text}</pre>
                      </div>`
                    : nothing;

            case 'user': {
                // A slash-command envelope renders as the raw "/name args" in a normal user
                // bubble (blue band), same as a typed message. A bare "/compact" has no envelope
                // and reaches here as plain text.
                const slashText = renderSlashCommand(this.text);
                const text = slashText || this.text;
                // The chips come from the entry, built once when the message arrived.
                const refs = slashText ? (EMPTY as readonly IdeContextRef[]) : this.ideRefs;
                // Skip empty user envelopes (e.g. tool_result-only messages,
                // consumed by the host for the tool row's OUT cell).
                if (
                    !text &&
                    this.images.length === 0 &&
                    this.files.length === 0 &&
                    refs.length === 0
                ) {
                    return nothing;
                }
                // A "[Request interrupted…]" notice gets an orange bar (not the blue
                // brand bar) so a stopped turn reads as interrupted, not a normal prompt.
                const interrupted = this.text.startsWith('[Request interrupted');
                const userCls = `cv-message user ${this.expanded ? 'expanded' : 'collapsible'}${interrupted ? ' interrupted' : ''}`;
                const hasChips = this.images.length > 0 || this.files.length > 0 || refs.length > 0;
                // The actions row sits OUTSIDE the bubble (below it, on the chat background) so it
                // doesn't inherit the user bubble's dark box.
                return html`
                    <div class=${userCls}>
                        ${
                            hasChips
                                ? html`<div class="cv-ide-chips">
                                      ${this.images.length > 0 ? this._renderImageChips(this.images) : nothing}
                                      ${this.files.length > 0 ? this._renderFileChips(this.files) : nothing}
                                      ${refs.length > 0 ? this._renderIdeChips(refs) : nothing}
                                  </div>`
                                : nothing
                        }
                        <div class="cv-msg-body">
                            <div class="md">
                                ${unsafeHTML(escapeHtml(text).replace(/\n/g, '<br>'))}
                            </div>
                        </div>
                        ${
                            this._isOverflowing || this.expanded
                                ? html`<button class="cv-show-more" @click=${this._onToggleExpand}>
                                      ${this.expanded ? 'Show less' : 'Show more'}
                                  </button>`
                                : nothing
                        }
                    </div>
                    ${interrupted ? nothing : this._renderActions()}
                `;
            }

            case 'assistant': {
                // Red when the API refused the turn. The frame looks like any other answer — the
                // CLI puts the error in the TEXT of a synthetic assistant — so the grey dot would
                // read as "answered fine". Same class the tool rows use for a failed tool.
                const dotClass = this.streaming
                    ? 'spinning'
                    : this.isError
                      ? 'dot-error'
                      : 'dot-gray';
                return html`
                    <div class="cv-message assistant">
                        <span class="cv-tool-row-dot ${dotClass}"></span>
                        <div class="cv-msg-body" @click=${this._onMdClick}>
                            ${
                                this.streaming
                                    ? html`<div class="md">
                                          ${unsafeHTML(this._streamingMarkdown(Date.now()))}
                                      </div>`
                                    : html`<div class="md">
                                          ${unsafeHTML(renderMarkdown(this.text))}
                                      </div>`
                            }
                        </div>
                    </div>
                `;
            }

            case 'error':
                return html`<div class="cv-message error">${this.text}</div>`;

            case 'result':
                return html`<div class="cv-message result">${this.text}</div>`;

            case 'status':
                return html`
                    <div class="cv-model-switch">
                        <span class="cv-model-switch-pill">${this.text}</span>
                    </div>
                `;

            case 'compact': {
                // Header is built from fields (no live/history divergence). The summary is
                // fetched lazily on first expand (compact-expand event → cv-app → `loaded`).
                const tk = this.preTokens > 0 ? ` · ${formatTokenCount(this.preTokens)} freed` : '';
                const header = `Compacted chat${this.trigger ? ` · ${this.trigger}` : ''}${tk}`;
                const body = !this.loaded
                    ? html`<div class="cv-compact-summary">Loading…</div>`
                    : this.summary
                      ? html`<div class="cv-compact-summary">${this.summary}</div>`
                      : html`<div class="cv-compact-summary">(no summary)</div>`;
                // Always an expandable <details> (closed by default, like VS Code) — the chevron is
                // always shown, even before the summary is fetched. @toggle fires on open only
                // (not on collapse) and dispatches compact-expand for cv-app to fetch/cache.
                return html`<details
                    class="cv-compact-details"
                    @toggle=${(e: Event) => {
                        if ((e.target as HTMLDetailsElement).open) {
                            this.dispatchEvent(
                                new CustomEvent('compact-expand', {
                                    detail: { uuid: this.uuid },
                                    bubbles: true,
                                    composed: true,
                                }),
                            );
                        }
                    }}
                >
                    <summary class="cv-compact-separator"><span>${header}</span></summary>
                    ${body}
                </details>`;
            }

            default:
                return nothing;
        }
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-message': CvMessage;
    }
}

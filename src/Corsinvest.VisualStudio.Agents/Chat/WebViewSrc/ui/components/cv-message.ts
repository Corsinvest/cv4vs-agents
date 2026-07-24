/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import './cv-copy-btn';
import './cv-msg-actions';
import './cv-attach-chip';
import { renderMarkdown, renderMarkdownStreaming } from '../../core/markdown';
import { escapeHtml } from '../../core/html';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { fetchChatImage, openChatDocument } from '../../core/lazy';
import { state as appState } from '../../core/state';
import { iconUrl } from '../../core/icon-url';
import { fileName, displayPath } from '../../core/path';
import { parseIdeContextTags } from '../../core/ide';
import { renderSlashCommand } from '../../core/slash-commands';
import { openLightbox } from '../../core/dialog-host';
import type {
    IdeContextRef,
    IdeFileNotification,
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
    // reflect: the sticky-user CSS keys off [role="user"] to pin only real user bubbles,
    // never a leading assistant/tool group that a history page split off from its user.
    @property({ reflect: true }) role: MessageRole = 'assistant';
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

    @property({ type: Boolean, reflect: true }) expanded = false;
    @state() private _isOverflowing = false;

    // Streaming markdown throttle: re-running the full marked→hljs→DOMPurify
    // pipeline on every token janks long answers, so cache the HTML and refresh
    // it at most every STREAM_MD_MS. A trailing timer guarantees the last chunk
    // renders even if it lands inside the throttle window.
    private static readonly STREAM_MD_MS = 75;
    private _streamHtml = '';
    private _streamText = '';
    private _streamAt = 0;
    private _streamTimer?: ReturnType<typeof setTimeout>;

    /** Throttled streaming markdown: returns cached HTML unless enough time has
     *  passed (or the text shrank/reset), scheduling a trailing refresh so the
     *  final partial chunk isn't stuck behind the throttle. */
    private _streamingMarkdown(now: number): string {
        if (this.text === this._streamText) {
            return this._streamHtml;
        }
        const due = now - this._streamAt >= CvMessage.STREAM_MD_MS;
        if (due || this._streamHtml === '') {
            this._streamHtml = renderMarkdownStreaming(this.text);
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

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        if (this._streamTimer) {
            clearTimeout(this._streamTimer);
            this._streamTimer = undefined;
        }
    }

    override updated(): void {
        if (this.role !== 'user') {
            return;
        }
        const el = this.querySelector('.cv-message.user') as HTMLElement | null;
        if (!el) {
            return;
        }
        // Measure natural scrollHeight with the cap removed, then re-apply
        // the cap (lineHeight * previewLines + paddingV) only when collapsed.
        el.style.maxHeight = '';
        el.style.minHeight = '';
        const cs = getComputedStyle(el);
        const lineHeight = parseFloat(cs.lineHeight) || 20;
        const paddingV = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom);
        const lines = appState.ui.previewLines || 3;
        const threshold = lineHeight * lines + paddingV;
        const naturalHeight = el.scrollHeight;
        // +4 tolerance absorbs sub-pixel lineHeight rounding.
        const overflows = naturalHeight > threshold + 4;
        // Drives the clip + fade CSS; only when truncated, so short bubbles keep their descenders.
        el.classList.toggle('is-overflowing', overflows && !this.expanded);
        if (overflows && !this.expanded) {
            el.style.maxHeight = `${threshold}px`;
            el.style.minHeight = `${threshold}px`;
        }
        if (overflows !== this._isOverflowing) {
            this._isOverflowing = overflows;
        }
    }

    /**
     * Bottom hover actions row. Fork only with a uuid (replayed from JSONL history — live messages
     * have none); Expand only for long user text. The fork bridge call lives in cv-msg-actions;
     * Expand toggles this element's state via the `toggle-expand` event.
     */
    private _renderActions() {
        const copyText = this.role === 'user' ? parseIdeContextTags(this.text).text : this.text;
        return html`<cv-msg-actions
            class="cv-msg-actions"
            .text=${copyText}
            .role=${this.role}
            .uuid=${this.uuid}
            .timestamp=${this.timestamp}
            .canFork=${this.role === 'user' && !!this.uuid}
            .canExpand=${this.role === 'user' && (this._isOverflowing || this.expanded)}
            .expanded=${this.expanded}
            @toggle-expand=${this._onToggleExpand}
        ></cv-msg-actions>`;
    }

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
    private _renderIdeChips(refs: IdeContextRef[]) {
        const wd = appState.workingDirectory;
        return refs.map((r) => {
            // Chip shows `name:start-end` (editor style, range only for a real
            // selection); tooltip carries the full relative path.
            const rel = displayPath(r.filePath, wd, appState.ui.showRelativePaths);
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
                // `text` by buildUserEntry) — a centered berry pill, not a user bubble.
                return this.text
                    ? html`<div class="cv-message slash-result${this.isError ? ' error' : ''}">
                          <pre>${this.text}</pre>
                      </div>`
                    : nothing;

            case 'user': {
                // A slash-command envelope renders as the raw "/name args" in a normal user
                // bubble (blue band), same as a typed message. A bare "/compact" has no envelope
                // and flows through parseIdeContextTags unchanged.
                const slashText = renderSlashCommand(this.text);
                // Strip CLI-injected <ide_*> tags and render them as chips
                // above the bubble instead of showing them verbatim.
                const { text, refs } = slashText
                    ? { text: slashText, refs: [] as IdeContextRef[] }
                    : parseIdeContextTags(this.text);
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
                        ${interrupted ? nothing : this._renderActions()}
                    </div>
                `;
            }

            case 'assistant': {
                const dotClass = this.streaming ? 'spinning' : 'dot-gray';
                return html`
                    <div class="cv-message assistant">
                        <span class="cv-tool-row-dot ${dotClass}"></span>
                        <div class="cv-msg-body">
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
                        ${!this.streaming ? this._renderActions() : nothing}
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
                const tk =
                    this.preTokens > 0
                        ? ` · ${Math.round(this.preTokens / 1000)}k tokens freed`
                        : '';
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

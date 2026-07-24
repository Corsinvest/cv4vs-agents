/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import ArrowExpandAll16Regular from '@fluentui/svg-icons/icons/arrow_expand_all_16_regular.svg';
import ArrowCollapseAll16Regular from '@fluentui/svg-icons/icons/arrow_collapse_all_16_regular.svg';
import './cv-message';
import './cv-thinking';
import './cv-copy-btn';
import type { ToolStatus, ToolUseData, UiEntry } from '../../core/types';
import { makeRenderer } from '../tool-renderers';
import { BridgeToolHost, cleanResult } from '../tool-renderers/tool-host';
import type { ToolRowState } from '../tool-renderers/types';
import { state as appState } from '../../core/state';

// Re-export so existing consumers (e.g. cv-app) can keep importing from here.
export type { ToolStatus, ToolUseData } from '../../core/types';

/**
 * Tool call rendered inline inside an assistant message. The component owns the
 * Lit state (expand, properties) and is the ToolHost the renderer talks to; the
 * resolved renderer draws the whole row via makeRenderer(name, host).row().
 */
@customElement('cv-tool-row')
export class CvToolRow extends LitElement implements ToolRowState {
    @property({ attribute: false }) data!: ToolUseData;
    @property() status: ToolStatus = 'pending';
    @property() result = '';
    /** Full output line count (before preview clipping), 0 when empty; count-only renderers use it. */
    @property({ type: Number }) fullLineCount = 0;
    @property({ type: Number }) elapsedSec = 0;
    @property({ attribute: false }) subagentChildren: UiEntry[] = [];
    /** More children exist on disk beyond the (≤3) kept in subagentChildren. */
    @property({ type: Boolean }) hasMore = false;
    /** Sub-agent id (Agent tool), used to fetch the full transcript on expand. */
    @property() agentId = '';
    /** Show-all — owned by cv-app (UiToolEntry.showAll), read here. True shows the full
     *  list; false shows the last 3. NOT the row open/closed state (that's `_expanded`). */
    @property({ type: Boolean }) showAll = false;

    @state() private _expanded = false;
    // Guards the one-shot lazy preview fetch (history Agent expanded with no children yet).
    private _previewRequested = false;

    private _unsubSubagentTasks?: () => void;

    /** ToolRowState: the host reads/writes these. */
    get expanded(): boolean {
        return this._expanded;
    }
    clipsOutput = false;
    toggleExpanded(): void {
        this._expanded = !this._expanded;
    }
    get subagentChildCount(): number {
        return this.subagentChildren.length;
    }

    override createRenderRoot() {
        return this;
    }

    override connectedCallback() {
        super.connectedCallback();
        // Re-render when subagentTasks changes so AgentRenderer.header() picks up the active task.
        this._unsubSubagentTasks = appState.on('subagentTasks', () => this.requestUpdate());
    }

    override disconnectedCallback() {
        super.disconnectedCallback();
        this._unsubSubagentTasks?.();
        this._unsubSubagentTasks = undefined;
    }

    override render() {
        return makeRenderer(this.data?.name, new BridgeToolHost(this)).row();
    }

    override updated(): void {
        // A history Agent row expanded with no children yet → lazily fetch its ≤3 preview, once.
        // Live rows already hold children in memory, so this never fires there. The guard stops it
        // re-firing on every render while the fetch is in flight; it resets when the row collapses.
        if (this._expanded && this.agentId && this.subagentChildren.length === 0) {
            if (!this._previewRequested) {
                this._previewRequested = true;
                this.dispatchEvent(
                    new CustomEvent('subagent-toggle', {
                        detail: { agentId: this.agentId, expand: true, preview: true },
                        bubbles: true,
                        composed: true,
                    }),
                );
            }
        } else if (!this._expanded) {
            this._previewRequested = false;
        }
    }

    /** Concatenate the sub-agent's children into markdown, reusing the same raw
     *  strings the per-item copy buttons use: text entries keep their markdown
     *  (tables intact); tool entries are input + cleaned output. */
    private _subagentToMarkdown(): string {
        return this.subagentChildren
            .map((e) => {
                if (e.kind === 'text') {
                    return e.text;
                }
                const input = e.data?.input != null ? JSON.stringify(e.data.input) : '';
                const out = cleanResult(e.result, e.status === 'error');
                const head = `**${e.data?.name ?? 'tool'}**`;
                return [head, input, out].filter(Boolean).join('\n');
            })
            .filter(Boolean)
            .join('\n\n');
    }

    // "Show all" button: expand asks cv-app for the WHOLE transcript (preview:false), collapse
    // slices back to 3. In history cv-app fetches; in live it just shows what's already in memory.
    // (The first chevron-expand preview is signalled separately in updated().) A same-tree event.
    private _onToggleSubagent = (e: Event): void => {
        e.stopPropagation();
        this.dispatchEvent(
            new CustomEvent('subagent-toggle', {
                detail: { agentId: this.agentId, expand: !this.showAll, preview: false },
                bubbles: true,
                composed: true,
            }),
        );
    };

    /** Actions for the Agent header row (copy output + expand/reduce), exposed to the
     *  renderer via the host. Rendered on the row, not above the children — so there's no
     *  empty toolbar band. Expand is always offered while the box is expanded: even with
     *  ≤3 children the collapsed view caps the height and scrolls, so the user still needs
     *  a way to lift the cap and see the whole transcript. */
    componentHeaderActions() {
        if (this.subagentChildren.length === 0) {
            return nothing;
        }
        return html`
            <cv-copy-btn
                .text=${this._subagentToMarkdown()}
                title="Copy subagent output"
            ></cv-copy-btn>
            <button
                class="icon-btn"
                title=${this.showAll ? 'Reduce' : 'Show all'}
                @click=${this._onToggleSubagent}
            >
                ${unsafeHTML(
                    this.showAll ? ArrowCollapseAll16Regular : ArrowExpandAll16Regular,
                )}
            </button>
        `;
    }

    /** Nested child rows/messages (Agent tool). Lit-owned, so it stays in the
     *  component; the host exposes it to the renderer via renderChildren(). */
    renderChildren() {
        if (this.subagentChildren.length === 0) {
            return nothing;
        }
        // Collapsed shows the last 3; expanded shows whatever subagentChildren holds
        // (the full list once fetched). The "…" marker appears when more exist.
        const shown = this.showAll
            ? this.subagentChildren
            : this.subagentChildren.slice(-3);
        const hasToggle = this.hasMore || this.subagentChildren.length > 3;
        return html`
            <div
                class="cv-subagent-children ${this.showAll ? '' : 'cv-subagent-collapsed'}"
            >
                ${
                    hasToggle && !this.showAll
                        ? html`<div class="cv-subagent-more" title="Earlier children — Show all">
                              …
                          </div>`
                        : nothing
                }
                ${shown.map((c: UiEntry) =>
                    c.kind === 'text' && c.role === 'thinking'
                        ? html`<cv-thinking
                              .text=${c.text}
                              ?streaming=${!!c.streaming}
                              .tokens=${c.tokens ?? 0}
                              .durationMs=${c.durationMs ?? 0}
                              ?redacted=${!!c.redacted}
                          ></cv-thinking>`
                        : c.kind === 'text'
                          ? html`<cv-message
                                .role=${c.role}
                                .text=${c.text}
                                ?streaming=${c.role === 'assistant' ? !!c.streaming : false}
                                ?isError=${c.role === 'slash-result' ? c.isError : false}
                            ></cv-message>`
                          : html`<cv-tool-row
                                .data=${c.data}
                                .status=${c.status}
                                .result=${c.result}
                                .elapsedSec=${c.elapsedSec}
                                .subagentChildren=${c.subagentChildren ?? []}
                                .fullLineCount=${c.fullLineCount}
                                .agentId=${this.agentId}
                                .hasMore=${c.hasMore ?? false}
                                .showAll=${c.showAll ?? false}
                            ></cv-tool-row>`,
                )}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-tool-row': CvToolRow;
    }
}

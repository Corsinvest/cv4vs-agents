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
import { renderActionsRow } from '../helpers/actions-row';

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
    // Named childItems, not `children`: HTMLElement.children (the DOM child collection) is reserved.
    @property({ attribute: false }) childItems: UiEntry[] = [];
    /** More children exist on disk beyond the (≤3) kept in `childItems`. */
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
    get childCount(): number {
        return this.childItems.length;
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
        if (this._expanded && this.agentId && this.childItems.length === 0) {
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
    private _childrenToMarkdown(): string {
        return this.childItems
            .map((e: UiEntry) => {
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
    private _onToggleShowAll = (e: Event): void => {
        e.stopPropagation();
        this.dispatchEvent(
            new CustomEvent('subagent-toggle', {
                detail: { agentId: this.agentId, expand: !this.showAll, preview: false },
                bubbles: true,
                composed: true,
            }),
        );
    };

    /** Header action for the Agent row: Show all / Reduce only. Copy lives in the children
     *  footer (renderChildren) instead — next to where the transcript ends, matching a normal
     *  response's bottom actions row. Expand is always offered while the box is expanded: even
     *  with ≤3 children the collapsed view caps the height and scrolls, so the user still needs
     *  a way to lift the cap and see the whole transcript. */
    componentHeaderActions() {
        if (this.childItems.length === 0) {
            return nothing;
        }
        return html`
            <button
                class="icon-btn"
                title=${this.showAll ? 'Reduce' : 'Show all'}
                @click=${this._onToggleShowAll}
            >
                ${unsafeHTML(this.showAll ? ArrowCollapseAll16Regular : ArrowExpandAll16Regular)}
            </button>
        `;
    }

    /** Nested child rows/messages (Agent tool today; generic). Lit-owned, so it stays in the
     *  component; the host exposes it to the renderer via renderChildren(). */
    renderChildren() {
        if (this.childItems.length === 0) {
            return nothing;
        }
        // Collapsed shows the last 3; showAll shows whatever childItems holds (the full list
        // once fetched). The "…" marker appears when more exist.
        const shown = this.showAll ? this.childItems : this.childItems.slice(-3);
        const hasToggle = this.hasMore || this.childItems.length > 3;
        return html`
            <div class="cv-children">
                <div class="cv-children-scroll ${this.showAll ? '' : 'cv-children-collapsed'}">
                    ${
                        hasToggle && !this.showAll
                            ? html`<div
                                  class="cv-children-more"
                                  title="Earlier children — Show all"
                              >
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
                                    .childItems=${c.children?.items ?? []}
                                    .fullLineCount=${c.fullLineCount}
                                    .agentId=${this.agentId}
                                    .hasMore=${c.children?.hasMore ?? false}
                                    .showAll=${c.children?.showAll ?? false}
                                ></cv-tool-row>`,
                    )}
                </div>
                ${this._renderChildrenActions()}
            </div>
        `;
    }

    /** Footer actions for the whole sub-agent transcript: Copy (the full transcript) + the last
     *  child's "x ago" timestamp. Sits at the bottom of the children box and mirrors a normal
     *  response's bottom actions row — hover-gated via CSS (.cv-children-actions). */
    private _renderChildrenActions() {
        // The last child carries the freshest timestamp; entries without one (e.g. thinking) → 0.
        const ts = this.childItems.reduce(
            (m, c) => Math.max(m, ('timestamp' in c ? c.timestamp : 0) ?? 0),
            0,
        );
        return renderActionsRow(
            this._childrenToMarkdown(),
            ts,
            'Copy subagent output',
            'cv-children-actions',
        );
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-tool-row': CvToolRow;
    }
}

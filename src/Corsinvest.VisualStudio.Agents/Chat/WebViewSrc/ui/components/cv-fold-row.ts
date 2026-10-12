/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';

/**
 * Focus view: one row standing for a run of tool calls and thinking the view folds away. It only
 * reports and toggles: the folded rows themselves stay in the transcript, hidden in place, and
 * cv-app shows them when this one is open. Same markup as a tool row's chrome (tool-renderers/
 * base.ts), so chat.css draws both alike.
 */
@customElement('cv-fold-row')
export class CvFoldRow extends LitElement {
    @property({ type: String }) label = '';
    /** What the run is doing now ("Running Bash…"); empty once settled. */
    @property({ type: String }) liveLabel = '';
    @property({ type: Boolean }) failed = false;
    @property({ type: Boolean }) live = false;
    @property({ type: Boolean }) expanded = false;

    // Light DOM, like cv-tool-row: the tool-row rules live in chat.css.
    override createRenderRoot() {
        return this;
    }

    private _toggle = (): void => {
        this.dispatchEvent(new CustomEvent('cv-fold-toggle', { bubbles: true, composed: true }));
    };

    override render() {
        const dot = this.live ? 'spinning' : this.failed ? 'dot-error' : 'dot-done';
        return html`<div class="cv-tool-wrap">
            <div class="cv-tool-row" style="cursor:pointer" @click=${this._toggle}>
                <span class="cv-tool-row-dot ${dot}"></span>
                ${
                    this.liveLabel
                        ? html`<span class="cv-tool-row-name">${this.liveLabel}</span>`
                        : nothing
                }
                <span class="cv-tool-row-detail">${this.label}</span>
                <button
                    class="cv-icon-btn cv-tool-row-chev ${this.expanded ? 'expanded' : ''}"
                    aria-label=${this.expanded ? 'Collapse' : 'Expand'}
                    @click=${(e: Event) => {
                        e.stopPropagation();
                        this._toggle();
                    }}
                >
                    <span class="cv-icon cv-icon-chevron-down"></span>
                </button>
            </div>
        </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-fold-row': CvFoldRow;
    }
}

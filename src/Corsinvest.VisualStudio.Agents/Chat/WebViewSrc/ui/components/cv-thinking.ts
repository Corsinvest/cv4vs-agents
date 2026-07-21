/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { renderMarkdown } from '../../core/markdown';
import { statusDotStyles } from '../styles/shared';

// <1000 → integer; >=1000 → X.Yk (one decimal). Mirrors VS Code's token format.
function fmtTokens(n: number): string {
    return n < 1000 ? String(n) : `${(n / 1000).toFixed(1)}k`;
}

/**
 * The model's reasoning block. Collapsed `<details>` by default; the token badge is the only
 * activity indicator while streaming (no spinner). `redacted` (cipher-only, no text) or empty
 * text renders as a static, non-expandable label — there is nothing to show inside.
 */
// Value props (not a single `entry` object): Lit dirty-checks by reference, so the entry is mutated
// in place during streaming — passing individual values makes each delta actually re-render.
@customElement('cv-thinking')
export class CvThinking extends LitElement {
    @property({ type: String }) text = '';
    @property({ type: Boolean }) streaming = false;
    @property({ type: Number }) tokens = 0;
    @property({ type: Number }) durationMs = 0;
    @property({ type: Boolean }) redacted = false;

    static override styles = [
        statusDotStyles,
        css`
            :host {
                display: block;
            }
            details {
                margin: 2px 0;
            }
            summary {
                display: flex;
                align-items: center;
                gap: 6px;
                justify-content: flex-start;
                cursor: pointer;
                list-style: none;
                user-select: none;
                font-style: italic;
                opacity: 0.8;
                color: var(--colorNeutralForeground3);
            }
            summary::-webkit-details-marker {
                display: none;
            }
            .tokens {
                white-space: nowrap;
                font-variant-numeric: tabular-nums;
            }
            .chevron {
                /* Full opacity so it stays visible against the dimmed (0.8) italic summary. Collapsed
               points right (-90°); open points down. */
                opacity: 1;
                font-size: 10px;
                transform: rotate(-90deg);
                transition: transform 0.15s;
            }
            details[open] .chevron {
                transform: rotate(0deg);
            }
            .body {
                /* Indent under the label (past the dot + gap: ~8px dot + 6px gap), like the assistant
               message body aligns under its name. */
                padding-left: 14px;
                margin-top: 4px;
                color: var(--colorNeutralForeground3);
                font-weight: 400;
            }
            .static {
                display: flex;
                align-items: center;
                gap: 6px;
                font-style: italic;
                opacity: 0.8;
                color: var(--colorNeutralForeground3);
            }
        `,
    ];

    override render() {
        const label = this.streaming
            ? 'Thinking…'
            : this.durationMs
              ? `Thought for ${Math.round(this.durationMs / 1000)}s`
              : 'Thinking';
        // Blue blinking while thinking; once done it just drops `.active` and stays neutral gray (no
        // `.done` class — a finished thought isn't a "success", so no green).
        const dot = html`<span class="cv-dot ${this.streaming ? 'active' : ''}"></span>`;
        // redacted or no text → static, non-expandable (matches VS Code).
        if (this.redacted || !this.text?.trim()) {
            return html`<div class="static">${dot}${label}</div>`;
        }
        // Token estimate stays visible after the turn too (e.g. "Thought for 1s · 88"), not only while
        // streaming — a short think would otherwise flash the count and lose it.
        const tokens = this.tokens
            ? html`<span class="tokens">· ${fmtTokens(this.tokens)}</span>`
            : nothing;
        return html`
            <details>
                <summary>
                    ${dot}
                    <span class="label">${label}</span>
                    ${tokens}
                    <span class="chevron">▾</span>
                </summary>
                <div class="body">${unsafeHTML(renderMarkdown(this.text))}</div>
            </details>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-thinking': CvThinking;
    }
}

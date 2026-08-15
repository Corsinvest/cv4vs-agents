/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { renderMarkdown } from '../../core/markdown';
import { onTick, type UnsubscribeTick } from '../../core/tick';
import { formatTokenCount, formatDuration } from '../helpers/format';
import { statusDotStyles } from '../styles/shared';
import ChevronDown16Regular from '@fluentui/svg-icons/icons/chevron_down_16_regular.svg';
import ChevronUp16Regular from '@fluentui/svg-icons/icons/chevron_up_16_regular.svg';

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
    /** When the block started (epoch ms), so the label can count up while it streams instead of
     *  only reporting the total at the end. The entry carries it already — cv-app stamps it on
     *  creation and subtracts it on thinkingEnded to get durationMs. */
    @property({ type: Number }) startedAt = 0;

    /** Re-render tick while streaming. Only a clock: the elapsed value is derived from startedAt,
     *  so a missed tick shows the right number late, never a wrong one. */
    @state() private _now = 0;
    private _unsubscribeTick?: UnsubscribeTick;

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._unsubscribeTick?.();
        this._unsubscribeTick = undefined;
    }

    /** Subscribes only while the block is open-ended: a finished thought has durationMs and needs
     *  no clock, and the element outlives the streaming (it stays in the transcript) — so this
     *  follows `streaming`, not the element's lifetime. */
    override updated(): void {
        const wants = this.streaming && this.startedAt > 0;
        if (wants && !this._unsubscribeTick) {
            this._unsubscribeTick = onTick(() => (this._now = Date.now()));
        } else if (!wants && this._unsubscribeTick) {
            this._unsubscribeTick();
            this._unsubscribeTick = undefined;
        }
    }

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
            /* The separator belongs to the CSS, not the text: summary is a flex row with a gap, so a
               "· " written into the span would put the gap on one side and the literal space on the
               other, and the dot reads as detached from what follows it. */
            .tokens::before {
                content: '·';
                margin-right: 6px;
            }
            .chevron {
                /* Full opacity so it stays visible against the dimmed (0.8) italic summary.
                   Down when collapsed, up when open — same single-chevron toggle as the tool
                   rows and expandable messages (no double chevron, no tree-style rotation). */
                opacity: 1;
                display: inline-flex;
                width: 16px;
                height: 16px;
            }
            .chevron .up {
                display: none;
            }
            details[open] .chevron .down {
                display: none;
            }
            details[open] .chevron .up {
                display: inline-flex;
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
        // While streaming the count comes from startedAt (the tick above only forces the re-render);
        // once done, from the durationMs the entry was stamped with. `_now` is read here so Lit sees
        // the dependency — without it the tick would fire against nothing.
        const live =
            this.streaming && this.startedAt > 0 ? (this._now || Date.now()) - this.startedAt : 0;
        const label = this.streaming
            ? live > 0
                ? `Thinking… ${formatDuration(live)}`
                : 'Thinking…'
            : this.durationMs
              ? `Thought for ${formatDuration(this.durationMs)}`
              : 'Thinking';
        // Blue blinking while thinking; once done it just drops `.active` and stays neutral gray (no
        // `.done` class — a finished thought isn't a "success", so no green).
        const dot = html`<span class="cv-dot ${this.streaming ? 'active' : ''}"></span>`;
        // redacted or no text → static, non-expandable (matches VS Code).
        if (this.redacted || !this.text?.trim()) {
            return html`<div class="static">${dot}${label}</div>`;
        }
        // Token estimate stays visible after the turn too (e.g. "Thought for 1s · ~88 tok"), not only
        // while streaming — a short think would otherwise flash the count and lose it. Same unit as
        // every other count in the chat, tilde included: the CLI sends this one as
        // `estimated_tokens`, so it is not the measured figure the response row shows.
        const tokens = this.tokens
            ? html`<span class="tokens">${formatTokenCount(this.tokens, true)}</span>`
            : nothing;
        return html`
            <details>
                <summary>
                    ${dot}
                    <span class="label">${label}</span>
                    ${tokens}
                    <span class="chevron"
                        ><span class="down">${unsafeHTML(ChevronDown16Regular)}</span
                        ><span class="up">${unsafeHTML(ChevronUp16Regular)}</span></span
                    >
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

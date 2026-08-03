/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { iconStyles } from '../styles/shared';

/** One choice in a segmented control. `value` (the key) is what `change` reports. */
export interface SegOption<V extends string = string> {
    /** The key reported on selection (a DTO union value). */
    value: V;
    /** Text shown on the button (hidden when the host has `icon-only`). */
    label: string;
    /** Optional leading SVG (string), placed in the button's `start` slot. */
    icon?: string;
    /** Optional tooltip; falls back to `label`. Useful for short/icon-only labels. */
    title?: string;
}

/**
 * Segmented control: a row of `<fluent-button>`s joined into one bar, exactly one
 * active. Fluent stays pure — the active button is `appearance="primary"` (filled
 * accent) and the rest `subtle`; only layout (join + end radii) is styled here.
 *
 * Buttons size to their content. Emits `change` (CustomEvent, `detail.value`) on
 * selection; re-selecting the active option is a no-op. `icon-size` overrides the
 * 20px Fluent draws a slotted glyph at, for rows where that reads too heavy.
 *
 *   <cv-segmented
 *       .options=${[{value:'all',label:'All'}, …]}
 *       .activeValue=${current}
 *       icon-size="14"
 *       @change=${(e) => onChange(e.detail.value)}
 *   ></cv-segmented>
 */
@customElement('cv-segmented')
export class CvSegmented<V extends string = string> extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: inline-flex;
            }
            .seg {
                display: inline-flex;
            }
            /* Join the buttons: square inner corners, rounded outer ends only, and tighten the
           default small-button padding so segments hug their content (only layout — Fluent pure). */
            .seg fluent-button::part(control) {
                border-radius: 0;
                min-width: auto;
                padding-inline: 10px;
            }
            .seg.icon-only fluent-button::part(control) {
                padding-inline: 8px;
            }
            .seg fluent-button:first-child::part(control) {
                border-top-left-radius: var(--borderRadiusMedium);
                border-bottom-left-radius: var(--borderRadiusMedium);
            }
            .seg fluent-button:last-child::part(control) {
                border-top-right-radius: var(--borderRadiusMedium);
                border-bottom-right-radius: var(--borderRadiusMedium);
            }
            /* Sized only when icon-size was given: the fallback of unset leaves the glyph size
               Fluent picked in place, so nothing that already looks right moves. The slot wrapper
               shrinks with the glyph, or the button keeps the taller line box around it. */
            .seg [slot='start'] {
                display: inline-flex;
                align-items: center;
                width: var(--seg-icon, unset);
                height: var(--seg-icon, unset);
            }
            .seg [slot='start'] svg {
                width: var(--seg-icon, unset);
                height: var(--seg-icon, unset);
            }
            fluent-tooltip {
                padding: 4px 8px;
                white-space: nowrap;
            }
        `,
    ];

    @property({ attribute: false }) options: SegOption<V>[] = [];
    @property({ attribute: false }) activeValue?: V;
    /** Show only the icon (label becomes the tooltip). Needs each option to have an `icon`. */
    @property({ type: Boolean, attribute: 'icon-only' }) iconOnly = false;
    /** Glyph size in px. Fluent draws a slotted icon at 20px, which is a lot next to a small
     *  button's text; unset leaves that alone so nothing that already looks right moves. */
    @property({ type: Number, attribute: 'icon-size' }) iconSize?: number;

    private _select(v: V): void {
        if (v === this.activeValue) {
            return;
        }
        this.dispatchEvent(
            new CustomEvent('change', {
                detail: { value: v },
                bubbles: true,
                composed: true,
            }),
        );
    }

    override render(): TemplateResult {
        return html`
            <div
                class="seg ${this.iconOnly ? 'icon-only' : ''}"
                style=${this.iconSize ? `--seg-icon: ${this.iconSize}px` : nothing}
                role="group"
            >
                ${this.options.map((o) => {
                    // A fluent-tooltip anchored by id gives a reliable tooltip (the button's own
                    // `title` attribute isn't surfaced across the shadow boundary). Always show it —
                    // it's essential icon-only, and a nicety for short labels (30d → "Last 30 days").
                    const id = `seg-${o.value}`;
                    const tip = o.title ?? o.label;
                    return html`
                        <fluent-button
                            id=${id}
                            appearance=${o.value === this.activeValue ? 'primary' : 'subtle'}
                            size="small"
                            aria-pressed=${o.value === this.activeValue}
                            @click=${() => this._select(o.value)}
                        >
                            ${o.icon ? html`<span slot="start">${unsafeHTML(o.icon)}</span>` : nothing}
                            ${this.iconOnly ? nothing : o.label}
                        </fluent-button>
                        <fluent-tooltip anchor=${id}>${tip}</fluent-tooltip>
                    `;
                })}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-segmented': CvSegmented;
    }
}

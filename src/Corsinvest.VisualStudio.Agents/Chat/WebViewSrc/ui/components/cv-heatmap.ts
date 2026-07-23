/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { styleMap } from 'lit/directives/style-map.js';

/** One heatmap cell. `intensity` 0–4 maps to the internal blue scale (0 = empty grey);
 *  `empty` renders a neutral placeholder (e.g. future days). `tip` is a rich hover card (rendered
 *  in a single floating overlay that follows the mouse); `title` is a plain-text fallback. */
export interface HeatmapCell {
    intensity: number;
    empty?: boolean;
    title?: string;
    tip?: TemplateResult;
}

/** A legend: caption at the ends + the intensity levels shown as swatches. */
export interface HeatmapLegend {
    less?: string;
    more?: string;
    /** Intensity levels to show as swatches, low→high (e.g. [0,1,2,3,4]). */
    levels: number[];
}

/**
 * Generic square-grid heatmap: columns of equal-length cells, each a colored square with an
 * optional tooltip, plus an optional Less→More legend. The blue intensity scale (0–4) lives
 * inside the component — callers pass a semantic intensity, not a color. Shadow DOM + static
 * styles (Lit standard): the Fluent theme tokens still reach in as they're inherited CSS
 * custom properties.
 */
@customElement('cv-heatmap')
export class CvHeatmap extends LitElement {
    static override styles = css`
        :host {
            display: block;
        }
        /* Intensity scale (0 = none → 4 = busiest). Empty day = a light-grey square (theme
         * token); active days climb a blue ramp with clear steps. Explicit hex — the brand
         * ramp's steps were too close to tell apart, and a dark first step blended into the
         * empty grey. The grid sits on the dialog's dark-ish surface, so fixed blues read fine. */
        :host {
            --cv-heat-0: var(--colorNeutralStroke2);
            --cv-heat-1: #2b6cb0;
            --cv-heat-2: #3b82f6;
            --cv-heat-3: #60a5fa;
            --cv-heat-4: #93c5fd;
        }
        .grid {
            display: flex;
            flex-direction: column;
            gap: 6px;
        }
        .bars {
            display: flex;
            gap: 3px;
            overflow-x: auto; /* many weeks scroll horizontally instead of stretching the dialog */
        }
        .col {
            display: flex;
            flex-direction: column;
            gap: 3px;
        }
        .cell {
            width: 11px;
            height: 11px;
            border-radius: 2px;
            flex: 0 0 auto;
            background: var(--colorNeutralStroke2);
        }
        .cell.is-empty {
            background: transparent;
        }
        .legend {
            display: flex;
            align-items: center;
            gap: 4px;
            color: var(--colorNeutralForeground3);
            font-size: var(--fontSizeBase200);
        }
        /* One floating rich tooltip shared by every cell, positioned at the mouse. Cheaper than a
         * per-cell tooltip (the grid can hold 300+ cells) and gives the same rich card as the chart. */
        .cell:hover {
            outline: 1px solid var(--colorNeutralStroke1);
            outline-offset: 1px;
        }
        .tip {
            position: fixed;
            z-index: 10;
            pointer-events: none;
            padding: 6px 8px;
            border-radius: var(--borderRadiusMedium);
            background: var(--colorNeutralBackground1);
            border: 1px solid var(--colorNeutralStroke2);
            box-shadow: var(--shadow8);
            font-size: var(--fontSizeBase300);
        }
    `;

    /** Columns of cells; every column should hold the same number of rows. */
    @property({ attribute: false }) cols: HeatmapCell[][] = [];
    @property({ attribute: false }) legend: HeatmapLegend | null = null;

    // The cell currently hovered (its rich `tip` is shown) and where to anchor the floating card.
    @state() private _tip: TemplateResult | null = null;
    @state() private _tipX = 0;
    @state() private _tipY = 0;

    private _cell(c: HeatmapCell): TemplateResult {
        if (c.empty) {
            return html`<span class="cell is-empty"></span>`;
        }
        // Rich card (tip) follows the mouse; fall back to the native title when there's no tip.
        return html`<span
            class="cell"
            style="background:var(--cv-heat-${c.intensity})"
            title=${c.tip ? '' : (c.title ?? '')}
            @pointerenter=${c.tip ? (e: PointerEvent) => this._showTip(c.tip!, e) : nothing}
            @pointermove=${c.tip ? (e: PointerEvent) => this._moveTip(e) : nothing}
            @pointerleave=${c.tip ? () => this._hideTip() : nothing}
        ></span>`;
    }

    private _showTip(tip: TemplateResult, e: PointerEvent): void {
        this._tip = tip;
        this._moveTip(e);
    }

    private _moveTip(e: PointerEvent): void {
        // Offset from the cursor so the card doesn't sit under the pointer; flip left near the edge.
        const pad = 14;
        const x = e.clientX + pad;
        this._tipX = x + 220 > window.innerWidth ? e.clientX - 220 : x;
        this._tipY = e.clientY + pad;
    }

    private _hideTip(): void {
        this._tip = null;
    }

    override render() {
        if (this.cols.length === 0) {
            return nothing;
        }
        return html`
            <div class="grid">
                <div class="bars">
                    ${this.cols.map(
                        (col) => html` <div class="col">${col.map((c) => this._cell(c))}</div> `,
                    )}
                </div>
                ${
                    this.legend
                        ? html` <div class="legend">
                              ${this.legend.less ? html`<span>${this.legend.less}</span>` : nothing}
                              ${this.legend.levels.map(
                                  (lvl) =>
                                      html`<span
                                          class="cell"
                                          style="background:var(--cv-heat-${lvl})"
                                      ></span>`,
                              )}
                              ${this.legend.more ? html`<span>${this.legend.more}</span>` : nothing}
                          </div>`
                        : nothing
                }
            </div>
            ${
                this._tip
                    ? html`<div
                          class="tip"
                          style=${styleMap({ left: `${this._tipX}px`, top: `${this._tipY}px` })}
                      >
                          ${this._tip}
                      </div>`
                    : nothing
            }
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-heatmap': CvHeatmap;
    }
}

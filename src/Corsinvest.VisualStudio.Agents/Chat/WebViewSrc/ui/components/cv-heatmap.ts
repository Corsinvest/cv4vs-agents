/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';

/** One heatmap cell. `intensity` 0–4 maps to the internal blue scale (0 = empty grey);
 *  `empty` renders a neutral placeholder (e.g. future days); `title` is the hover tooltip. */
export interface HeatmapCell {
    intensity: number;
    empty?: boolean;
    title?: string;
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
    `;

    /** Columns of cells; every column should hold the same number of rows. */
    @property({ attribute: false }) cols: HeatmapCell[][] = [];
    @property({ attribute: false }) legend: HeatmapLegend | null = null;

    private _cell(c: HeatmapCell): TemplateResult {
        return c.empty
            ? html`<span class="cell is-empty"></span>`
            : html`<span
                  class="cell"
                  style="background:var(--cv-heat-${c.intensity})"
                  title=${c.title ?? ''}
              ></span>`;
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
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-heatmap': CvHeatmap;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';

/** One stop on the slider track. `value` is what `change` reports. */
export interface SliderStop<V = unknown> {
    value: V;
    /** Tooltip / accessible label for this stop. */
    label?: string;
    /** Tint the fill/knob with the accent colour when this stop is active
     *  (e.g. the purple "ultracode" stop past the max). */
    accent?: boolean;
}

/**
 * Segmented slider — a track with N evenly-spaced stops and a knob that snaps
 * to the active one (the "Effort (High)" control style). Pointer drag and
 * Left/Right/Home/End keys move the knob; each settle fires a `change` event
 * carrying the selected stop's `value`.
 *
 *   <cv-segmented-slider
 *       .stops=${stops}
 *       .activeValue=${value}
 *       label="Effort"
 *       @change=${onPick}
 *   ></cv-segmented-slider>
 *
 * Shadow DOM (own `<div>` markup + CSS) — not a restyled `<fluent-*>`.
 */
@customElement('cv-segmented-slider')
export class CvSegmentedSlider<V = unknown> extends LitElement {
    @property({ attribute: false }) stops: SliderStop<V>[] = [];
    @property({ attribute: false }) activeValue?: V;
    /** Prefix shown before the active label, e.g. "Effort" → "Effort (High)". */
    @property() label = '';

    static override styles = css`
        :host {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            font: inherit;
            user-select: none;
            /* Themable parts — the host row overrides these (e.g. on a brand
             * background) so the slider stays legible. Defaults track VS Code. */
            --cv-slider-track: var(--vscode-input-background, #3c3c3c);
            --cv-slider-fill: var(--vscode-button-background, #0e639c);
            --cv-slider-knob: var(--vscode-button-foreground, #fff);
            --cv-slider-dot: var(--vscode-descriptionForeground, #888);
            --cv-slider-border: transparent;
        }
        .lead {
            color: var(--vscode-descriptionForeground, #999);
            white-space: nowrap;
        }
        .lead strong {
            color: var(--vscode-foreground, #ccc);
            font-weight: 600;
        }
        .track {
            position: relative;
            height: 22px;
            min-width: 84px;
            flex: 0 0 auto;
            display: flex;
            align-items: center;
            padding: 0 11px;
            border-radius: 11px;
            cursor: pointer;
            background: var(--cv-slider-track);
            border: 1px solid var(--cv-slider-border);
            outline: none;
        }
        .track:focus-visible {
            box-shadow: 0 0 0 2px var(--vscode-focusBorder, #007fd4);
        }
        /* Accent stop (ultracode): purple fill/knob past the max. */
        .track.accent {
            --cv-slider-fill: #b180d7;
            --cv-slider-knob: #fff;
        }
        /* filled portion up to the active stop */
        .fill {
            position: absolute;
            left: 0;
            top: 0;
            bottom: 0;
            border-radius: 11px;
            background: var(--cv-slider-fill);
            transition: width 120ms ease;
            pointer-events: none;
        }
        .stops {
            position: relative;
            flex: 1;
            display: flex;
            justify-content: space-between;
            align-items: center;
        }
        .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--cv-slider-dot);
            opacity: 0.7;
            z-index: 1;
        }
        .dot.filled {
            background: color-mix(in srgb, var(--cv-slider-knob) 75%, transparent);
            opacity: 0.9;
        }
        .knob {
            position: absolute;
            top: 50%;
            width: 16px;
            height: 16px;
            border-radius: 50%;
            background: var(--cv-slider-knob);
            box-shadow: 0 1px 3px rgba(0, 0, 0, 0.5);
            transform: translate(-50%, -50%);
            transition: left 120ms ease;
            z-index: 2;
            pointer-events: none;
        }
    `;

    /** Index of the active stop; 0 when nothing matches. */
    private get _index(): number {
        const i = this.stops.findIndex((s) => s.value === this.activeValue);
        return i < 0 ? 0 : i;
    }

    private _commit(index: number): void {
        const clamped = Math.max(0, Math.min(this.stops.length - 1, index));
        const stop = this.stops[clamped];
        if (!stop || stop.value === this.activeValue) {
            return;
        }
        this.dispatchEvent(
            new CustomEvent<SliderStop<V>>('change', {
                detail: stop,
                bubbles: true,
                composed: true,
            }),
        );
    }

    /** Map a pointer x within the track to the nearest stop index. */
    private _indexFromPointer(clientX: number, track: HTMLElement): number {
        const r = track.getBoundingClientRect();
        const pad = 11;
        const usable = Math.max(1, r.width - pad * 2);
        const ratio = (clientX - r.left - pad) / usable;
        return Math.round(ratio * (this.stops.length - 1));
    }

    private _onPointerDown = (e: PointerEvent): void => {
        const track = e.currentTarget as HTMLElement;
        track.setPointerCapture(e.pointerId);
        this._commit(this._indexFromPointer(e.clientX, track));
        const move = (ev: PointerEvent) => this._commit(this._indexFromPointer(ev.clientX, track));
        const up = (ev: PointerEvent) => {
            track.releasePointerCapture(ev.pointerId);
            track.removeEventListener('pointermove', move);
            track.removeEventListener('pointerup', up);
        };
        track.addEventListener('pointermove', move);
        track.addEventListener('pointerup', up);
    };

    private _onKeyDown = (e: KeyboardEvent): void => {
        const i = this._index;
        switch (e.key) {
            case 'ArrowLeft':
            case 'ArrowDown':
                this._commit(i - 1);
                e.preventDefault();
                break;
            case 'ArrowRight':
            case 'ArrowUp':
                this._commit(i + 1);
                e.preventDefault();
                break;
            case 'Home':
                this._commit(0);
                e.preventDefault();
                break;
            case 'End':
                this._commit(this.stops.length - 1);
                e.preventDefault();
                break;
        }
    };

    override render() {
        const n = this.stops.length;
        const idx = this._index;
        const active = this.stops[idx];
        // knob/fill position as a percentage of the padded track span
        const pos = n > 1 ? (idx / (n - 1)) * 100 : 0;
        const fillPx = `calc(11px + (100% - 22px) * ${pos / 100})`;

        return html`
            ${
                this.label
                    ? html`<span class="lead"
                          >${this.label}${
                              active?.label ? html` <strong>(${active.label})</strong>` : nothing
                          }</span
                      >`
                    : nothing
            }
            <div
                class="track ${active?.accent ? 'accent' : ''}"
                role="slider"
                tabindex="0"
                aria-label=${this.label || 'selector'}
                aria-valuemin="0"
                aria-valuemax=${n - 1}
                aria-valuenow=${idx}
                aria-valuetext=${active?.label ?? ''}
                title=${active?.label ?? ''}
                @pointerdown=${this._onPointerDown}
                @keydown=${this._onKeyDown}
            >
                <div class="fill" style=${`width:${fillPx}`}></div>
                <div class="stops">
                    ${this.stops.map(
                        (s, i) =>
                            html`<span
                                class="dot ${i <= idx ? 'filled' : ''}"
                                title=${s.label ?? ''}
                            ></span>`,
                    )}
                </div>
                <div class="knob" style=${`left:${fillPx}`}></div>
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-segmented-slider': CvSegmentedSlider;
    }
}

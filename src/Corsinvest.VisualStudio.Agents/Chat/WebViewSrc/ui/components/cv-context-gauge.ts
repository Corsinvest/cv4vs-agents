/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing, svg } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { iconStyles } from '../styles/shared';
import { state as appState } from '../../core/state';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import {
    consumedTokens,
    contextPercent,
    formatTokens,
    autoCompactWindow,
    remainingPercent,
} from '../../core/ai-models';
import type { ContextUsageDto, SendPromptNotification } from '../../core/types';
import { openUsageDialog, openContextDialog, openStatsDialog } from '../../core/dialog-host';

// Color band by percent (Fluent palette tokens, so it tracks the theme).
function gaugeColor(percent: number): string {
    if (percent >= 85) {
        return 'var(--colorPaletteRedForeground1)';
    }
    if (percent >= 60) {
        return 'var(--colorPaletteDarkOrangeForeground1)';
    }
    return 'var(--colorPaletteGreenForeground1)';
}

// Same bands as gaugeColor, mapped to fluent-progress-bar's validation-state (success/warning/error).
function gaugeValidationState(percent: number): 'success' | 'warning' | 'error' {
    if (percent >= 85) {
        return 'error';
    }
    if (percent >= 60) {
        return 'warning';
    }
    return 'success';
}

const SIZE = 14;
const STROKE = 2;
const RADIUS = (SIZE - STROKE) / 2;
const CIRC = 2 * Math.PI * RADIUS;

/**
 * Donut gauge showing context-window consumption vs the active model's limit.
 * Reads `appState.contextUsage`/`currentModel`; hidden until the first turn.
 */
@customElement('cv-context-gauge')
export class CvContextGauge extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* Relative host so the click panel can be absolutely positioned above the ring — plain
           position:absolute, not CSS anchor-positioning (position-area is unreliable in the VS
           WebView2's Chromium: the top-layer popover jumps to the viewport corner). */
            :host {
                position: relative;
                display: inline-flex;
            }
            /* Ring hit-area, sized to hug the small ring exactly. Clickable (opens the panel). */
            .gauge {
                display: inline-flex;
                align-items: center;
                justify-content: center;
                width: 22px;
                height: 22px;
                border-radius: 4px;
                border: none;
                background: none;
                padding: 0;
                cursor: pointer;
            }
            .gauge:hover {
                background: var(--colorSubtleBackgroundHover, rgba(127, 127, 127, 0.15));
            }
            .gauge:active {
                background: var(--colorSubtleBackgroundPressed, rgba(127, 127, 127, 0.25));
            }
            .gauge svg {
                flex: 0 0 auto;
                display: block;
            }
            /* Small hover tooltip: just the % (one line). The detail + actions live in the click panel. */
            fluent-tooltip {
                padding: 4px 8px;
                white-space: nowrap;
            }

            /* Click panel: info + actions, absolutely positioned above the ring, left-aligned to it. */
            .popover {
                position: absolute;
                bottom: calc(100% + 4px);
                left: 0;
                z-index: 1000;
                padding: 8px 10px;
                width: 340px;
                max-width: 90vw;
                font-size: var(--fontSizeBase300);
                line-height: var(--lineHeightBase200);
                background: var(--colorNeutralBackground1);
                color: var(--colorNeutralForeground1);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                box-shadow: var(--shadow16);
            }
            .popover[hidden] {
                display: none;
            }
            .tip-head {
                font-weight: var(--fontWeightSemibold);
            }
            /* fluent-progress-bar stays pure — only vertical spacing (colour comes from validation-state,
           matching the donut's green/amber/red bands). */
            .bar {
                margin: 8px 0 2px;
            }
            .bar-legend {
                display: flex;
                justify-content: space-between;
                font-size: 0.82em;
                color: var(--colorNeutralForeground3);
                font-variant-numeric: tabular-nums;
                margin-bottom: 4px;
            }
            .tip-actions {
                display: flex;
                flex-wrap: nowrap;
                gap: 14px;
                margin-top: 6px;
                padding-top: 6px;
                border-top: 1px solid var(--colorNeutralStroke2);
            }
            .tip-actions fluent-link {
                white-space: nowrap;
            }
        `,
    ];

    @state() private _usage: ContextUsageDto | null = appState.contextUsage;
    @state() private _window = appState.contextWindow;
    @state() private _open = false;

    private _offUsage?: () => void;
    private _offWindow?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._offUsage = appState.on('contextUsage', (v) => {
            this._usage = v;
        });
        this._offWindow = appState.on('contextWindow', (v) => {
            this._window = v;
        });
        // Light dismiss: a click anywhere outside the gauge closes the panel.
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offUsage?.();
        this._offWindow?.();
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
    }

    private _onDocPointerDown = (e: PointerEvent): void => {
        if (!this._open) {
            return;
        }
        // composedPath crosses the shadow boundary; if the click isn't on us, close.
        if (!e.composedPath().includes(this)) {
            this._open = false;
        }
    };

    /** Compact the conversation via the CLI's `/compact` command (mirrors cv-prompt._dispatch). */
    private _onCompact = (): void => {
        if (appState.isBusy) {
            return;
        }
        appState.isBusy = true;
        bridge.sendNotification<SendPromptNotification>(Msg.fromWebView.cli.sendPrompt, {
            text: '/compact',
            attachments: [],
            uuid: crypto.randomUUID(),
        });
    };

    private _toggle = (): void => {
        this._open = !this._open;
    };

    // Run an action, then close the panel (like a menu item).
    private _act(fn: () => void): void {
        fn();
        this._open = false;
    }

    // Open each panel directly via dialog-host (no parent host needed).
    private _onViewUsage = (): void => openUsageDialog();
    private _onViewContext = (): void => openContextDialog();
    private _onViewStats = (): void => openStatsDialog();

    override render() {
        const u = this._usage;
        // Hidden until both the usage and the model's real window are known
        // (the window arrives with the first result), like VS Code.
        if (!u || this._window <= 0) {
            return nothing;
        }
        // Gauge fill tracks raw consumption of the model's full window; the
        // tooltip headline tracks the AUTO-COMPACT window (limit − output − buffer),
        // matching VS Code's "{n}% of context remaining until auto-compact".
        const percent = contextPercent(u);
        const color = gaugeColor(percent);
        // Arc: stroke-dashoffset runs CIRC (empty) → 0 (full).
        const offset = CIRC * (1 - percent / 100);

        const used = consumedTokens(u);
        const limit = appState.contextWindow;
        const remainingPct = remainingPercent(u);
        const window = autoCompactWindow();

        // Clickable ring (<button>) → opens a light-dismiss popover with the usage detail + actions
        // (like cv-subagent-chip). Click, not hover: the panel is interactive (Compact/dialogs) and
        // a hover panel above the ring overlaps the composer / vanishes as the mouse moves to it.
        return html`
            <button
                id="cv-context-gauge-btn"
                class="gauge"
                type="button"
                aria-label="Context usage"
                @click=${this._toggle}
            >
                ${svg`
                    <svg width=${SIZE} height=${SIZE} viewBox="0 0 ${SIZE} ${SIZE}">
                        <circle
                            cx=${SIZE / 2}
                            cy=${SIZE / 2}
                            r=${RADIUS}
                            fill="none"
                            stroke="var(--colorNeutralStroke2)"
                            stroke-width=${STROKE}
                        />
                        <circle
                            cx=${SIZE / 2}
                            cy=${SIZE / 2}
                            r=${RADIUS}
                            fill="none"
                            stroke=${color}
                            stroke-width=${STROKE}
                            stroke-dasharray=${CIRC}
                            stroke-dashoffset=${offset}
                            stroke-linecap="round"
                            transform="rotate(-90 ${SIZE / 2} ${SIZE / 2})"
                        />
                    </svg>
                `}
            </button>
            <div id="cv-gauge-popover" class="popover" ?hidden=${!this._open}>
                <div class="tip-head">
                    ${remainingPct.toFixed(0)}% of context remaining until auto-compact
                </div>
                <fluent-progress-bar
                    class="bar"
                    min="0"
                    max="100"
                    value=${Math.min(100, (used / limit) * 100)}
                    validation-state=${gaugeValidationState(percent)}
                ></fluent-progress-bar>
                <div class="bar-legend">
                    <span>${formatTokens(used)} used</span>
                    <span>${formatTokens(window)} before compact</span>
                    <span>${formatTokens(limit)} total</span>
                </div>
                <div class="tip-actions">
                    <fluent-button
                        appearance="transparent"
                        size="small"
                        @click=${(): void => this._act(this._onCompact)}
                        >Compact</fluent-button
                    >
                    <fluent-button
                        appearance="transparent"
                        size="small"
                        @click=${(): void => this._act(this._onViewUsage)}
                        >Usage…</fluent-button
                    >
                    <fluent-button
                        appearance="transparent"
                        size="small"
                        @click=${(): void => this._act(this._onViewContext)}
                        >Context…</fluent-button
                    >
                    <fluent-button
                        appearance="transparent"
                        size="small"
                        @click=${(): void => this._act(this._onViewStats)}
                        >Statistics…</fluent-button
                    >
                </div>
            </div>
            <fluent-tooltip anchor="cv-context-gauge-btn" positioning="above-end">
                ${remainingPct.toFixed(0)}% of context remaining
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-context-gauge': CvContextGauge;
    }
}

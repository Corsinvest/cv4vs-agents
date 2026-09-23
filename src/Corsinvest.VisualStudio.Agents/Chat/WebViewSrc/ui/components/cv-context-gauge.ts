/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, svg, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// The same two the VS menu's Analytics group uses (AgentsPackage.vsct): Statistics is a
// bar chart there, Context usage a doughnut. Usage keeps the gauge, which command-icons
// already maps to the same glyph.
import DataBarVertical16Regular from '@fluentui/svg-icons/icons/data_bar_vertical_16_regular.svg';
import DataPie16Regular from '@fluentui/svg-icons/icons/data_pie_16_regular.svg';
import ClockWarning16Regular from '@fluentui/svg-icons/icons/clock_warning_16_regular.svg';
import { iconForCommandName } from '../../core/commands/command-icons';
import { iconStyles, tooltipStyles } from '../styles/shared';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import {
    consumedTokens,
    contextPercent,
    autoCompactWindow,
    remainingPercent,
    cacheState,
    type CacheState,
} from '../../core/ai-models';
import { formatTokens } from '../helpers/format';
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

// Coarse on purpose: the reading is an estimate, and a cache dead for days is no more useful
// stated to the minute.
function formatIdle(ms: number): string {
    const minutes = Math.max(0, Math.floor(ms / 60000));
    if (minutes < 60) {
        return `${minutes}m`;
    }
    const hours = Math.floor(minutes / 60);
    return hours < 24 ? `${hours}h ${minutes % 60}m` : `${Math.floor(hours / 24)}d ${hours % 24}h`;
}

function cacheTooltip(state: CacheState): string {
    switch (state.kind) {
        case 'unknown':
            return '';
        case 'warm':
            return `Prompt cache warm, about ${state.minutesLeft} min left.`;
        case 'cold':
            return (
                `Prompt cache likely expired (idle ${formatIdle(state.idleMs)}).\n` +
                `Your next message re-caches about ${formatTokens(state.recacheTokens)} tokens.`
            );
    }
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
        tooltipStyles,
        css`
            :host {
                display: inline-flex;
                align-items: center;
            }
            /* Amber, not red: the next message costs more, but nothing is broken — and the ring's
               own red already means the context is nearly full. */
            .cache-warning {
                display: inline-flex;
                color: var(--colorPaletteDarkOrangeForeground1);
            }
            /* 14px like every other glyph in the row: iconStyles only pins the ones inside an
               icon-only button, and this one is a bare span. */
            .cache-warning svg {
                display: block;
                width: 14px;
                height: 14px;
            }
            /* Fluent's own button, cut to the row's density: even size="small" is padded for a
               control standing alone. The ring is 14px like every other glyph here. */
            .gauge {
                padding: 3px;
                min-width: 0;
            }
            /* Wraps the ring so it can be the tooltip's anchor while the button stays the menu's
               — see cv-attach-menu for what happens when they share one. */
            .tip-anchor {
                display: inline-flex;
            }
            .gauge svg {
                display: block;
            }
            /* The reading at the top of the menu: not an action, so a plain div rather than a
               menu-item — the focusgroup skips what has no menuitem role, and arrow keys land on
               the four actions below. */
            /* Colour set here, not inherited: the div is slotted into fluent-menu-list, whose own
               foreground doesn't reach a plain child — it would fall back to the UA black. */
            .reading {
                padding: 6px 10px 8px;
                min-width: 280px;
                font-size: var(--fontSizeBase200);
                line-height: var(--lineHeightBase200);
                color: var(--colorNeutralForeground1);
                border-bottom: 1px solid var(--colorNeutralStroke2);
            }
            .reading-head {
                font-weight: var(--fontWeightSemibold);
            }
            /* fluent-progress-bar stays pure — only vertical spacing (colour comes from
               validation-state, matching the donut's green/amber/red bands). */
            .bar {
                margin: 8px 0 2px;
            }
            .bar-legend {
                display: flex;
                justify-content: space-between;
                font-size: 0.82em;
                color: var(--colorNeutralForeground3);
                font-variant-numeric: tabular-nums;
            }
            /* Hung from the ring's right edge, not its left: Fluent aligns the list's start to the
               trigger's, which on the last control in the row throws 280px of menu out to the left
               and leaves the pointer crossing open air to reach it. Flush with that edge — send
               used to sit past the ring and the list hung into its width, but send now lives inside
               the field and there is nothing to the right to lean on. */
            fluent-menu-list {
                inset-inline-start: unset;
                inset-inline-end: anchor(self-end);
            }
            .sep {
                height: 1px;
                margin: 4px 0;
                background: var(--colorNeutralStroke2);
            }
            /* Centre the glyph in the item's start cell — Fluent's default hugs the cell edge,
               too tight at this density. Same rule as cv-attach-menu. */
            fluent-menu-item [slot='start'] {
                display: inline-flex;
                align-items: center;
                justify-content: center;
            }
        `,
    ];

    @state() private _usage: ContextUsageDto | null = appState.contextUsage;
    @state() private _window = appState.contextWindow;
    @state() private _cacheAnchor = appState.cacheAnchorMs;

    private readonly _subs = new StateSubscriptions(this);

    constructor() {
        super();
        this._subs.on('contextUsage', (v) => {
            this._usage = v;
        });
        this._subs.on('cacheAnchorMs', (v) => {
            this._cacheAnchor = v;
        });
        this._subs.on('contextWindow', (v) => {
            this._window = v;
        });
    }

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

    // Open each panel directly via dialog-host (no parent host needed).
    private _onViewUsage = (): void => openUsageDialog();
    private _onViewContext = (): void => openContextDialog();
    private _onViewStats = (): void => openStatsDialog();

    override render() {
        const u = this._usage;
        // The real window only arrives with the first result, so a fresh (or just-resumed) session
        // has no numbers yet. Show the ring anyway, empty: it keeps its slot in the toolbar instead
        // of appearing after the first turn and shifting everything beside it.
        const known = !!u && this._window > 0;
        // Gauge fill tracks raw consumption of the model's full window; the
        // tooltip headline tracks the AUTO-COMPACT window (limit − output − buffer),
        // matching VS Code's "{n}% of context remaining until auto-compact".
        const percent = known ? contextPercent(u) : 0;
        const color = gaugeColor(percent);
        // Arc: stroke-dashoffset runs CIRC (empty) → 0 (full).
        const offset = CIRC * (1 - percent / 100);

        // No timer: the reading is only ever seen while the tooltip or menu is open, and
        // opening one renders.
        const cache = cacheState(u, this._cacheAnchor, Date.now());
        const cacheText = cacheTooltip(cache);

        const used = known ? consumedTokens(u) : 0;
        const limit = appState.contextWindow;
        const remainingPct = known ? remainingPercent(u) : 100;
        const window = autoCompactWindow();

        // The ring opens a menu: the reading at the top, then what you can do about it. Click, not
        // hover — the items are actions, and a hover panel above the ring would vanish as the
        // mouse travelled to them.
        return html`
            <fluent-menu>
                <!-- id="menu-trigger" is load-bearing: fluent-menu-list anchors itself to
                     --menu-trigger, and the trigger's anchor-name comes from its id. The tooltip
                     hangs off the span inside instead — anchoring it here would overwrite that
                     name and drop the list at 0,0 (see cv-attach-menu). -->
                <fluent-button
                    id="menu-trigger"
                    slot="trigger"
                    class="gauge"
                    appearance="subtle"
                    shape="rounded"
                    size="small"
                    icon-only
                    aria-label="Context usage"
                >
                    <span id="gauge-tip" class="tip-anchor">
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
                            transform="rotate(-90 ${SIZE / 2} ${SIZE / 2})"
                        />
                    </svg>
                `}
                    </span>
                </fluent-button>
                <fluent-menu-list>
                    <!-- A plain div, not a menu-item: the reading is what the menu is for, but it
                         is not an action. Without role=menuitem the focusgroup skips it, so arrow
                         keys go straight to the four below. -->
                    <div class="reading">
                        <div class="reading-head">
                            ${
                                known
                                    ? `${remainingPct.toFixed(0)}% of context remaining until auto-compact`
                                    : 'Context usage is reported after the first reply'
                            }
                        </div>
                        <fluent-progress-bar
                            class="bar"
                            min="0"
                            max="100"
                            value=${limit > 0 ? Math.min(100, (used / limit) * 100) : 0}
                            validation-state=${gaugeValidationState(percent)}
                        ></fluent-progress-bar>
                        <div class="bar-legend">
                            <span>${formatTokens(used)} used</span>
                            <span>${formatTokens(window)} before compact</span>
                            <span>${formatTokens(limit)} total</span>
                        </div>
                    </div>
                    <fluent-menu-item @click=${this._onCompact}>
                        <span slot="start">${unsafeHTML(iconForCommandName('compact'))}</span>
                        Compact
                    </fluent-menu-item>
                    <!-- Compact acts on the conversation; the three below open a window to look at
                         it. A plain div, not fluent-divider: that one ships no package entry point
                         (only dist/esm), and a rule is a rule. -->
                    <div class="sep" role="separator"></div>
                    <!-- Statistics, Usage, Context usage — the order and the icons of the VS
                         menu's own Analytics group, so the two ways in read the same. No trailing
                         ellipsis, for the same reason: these open a window, they don't ask for
                         anything first. -->
                    <fluent-menu-item @click=${this._onViewStats}>
                        <span slot="start">${unsafeHTML(DataBarVertical16Regular)}</span>
                        Statistics
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onViewUsage}>
                        <span slot="start">${unsafeHTML(iconForCommandName('usage'))}</span>
                        Usage
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onViewContext}>
                        <span slot="start">${unsafeHTML(DataPie16Regular)}</span>
                        Context usage
                    </fluent-menu-item>
                </fluent-menu-list>
            </fluent-menu>
            ${
                // Beside the ring, not on it: the two readings answer different questions, and
                // drawn over the arc neither survived. Only shown cold — while the cache holds
                // there is nothing to act on, and the tooltip says so for anyone who looks.
                cache.kind === 'cold'
                    ? html`<span id="cache-tip" class="cache-warning" aria-label="Prompt cache"
                          >${unsafeHTML(ClockWarning16Regular)}</span
                      >`
                    : nothing
            }
            <!-- Named like the other triggers, because a ring on its own says nothing about what it
                 measures. "left" stays: the arc fills with what has been CONSUMED while the number
                 is what REMAINS, so a bare percentage would read as the opposite of the ring beside
                 it. Before the first result there is no number and the name stands alone — an empty
                 ring already says there is nothing to read yet. Nothing about clicking either: this
                 is a menu trigger, and the menu says what it offers when it opens. -->
            <fluent-tooltip anchor="gauge-tip" positioning="above-end"
                >${[
                    known ? `Context: ${remainingPct.toFixed(0)}% left` : 'Context',
                    // Only while there is no icon: cold, the icon beside it carries this, and
                    // saying it twice would answer the cache to someone pointing at the ring.
                    cache.kind === 'cold' ? '' : cacheText,
                ]
                    .filter(Boolean)
                    .join('\n')}</fluent-tooltip
            >
            ${
                cache.kind === 'cold'
                    ? html`<fluent-tooltip anchor="cache-tip" positioning="above-end"
                          >${cacheText}</fluent-tooltip
                      >`
                    : nothing
            }
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-context-gauge': CvContextGauge;
    }
}

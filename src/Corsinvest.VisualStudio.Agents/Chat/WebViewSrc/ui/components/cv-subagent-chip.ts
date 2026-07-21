/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Bot16Regular from '@fluentui/svg-icons/icons/bot_16_regular.svg';
import Stop16Filled from '@fluentui/svg-icons/icons/stop_16_filled.svg';
import { formatTokens } from '../../core/ai-models';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { iconStyles, statusDotStyles } from '../styles/shared';
import type { SubagentTask, SubagentCancelNotification } from '../../core/types';

/** A pulsing chip in the input row showing the count of active sub-agents.
 *  Hidden when none. Click opens a light-dismiss popover (like the permission
 *  selector — no backdrop) listing each agent with a Stop, plus a Stop-all.
 *  Shadow DOM + static styles (Lit standard). The button and popover live
 *  together in the same shadow root so CSS anchor-positioning resolves locally
 *  (both nodes share this render tree; a separate element wouldn't anchor). */
@customElement('cv-subagent-chip')
export class CvSubagentChip extends LitElement {
    static override styles = [
        iconStyles,
        statusDotStyles,
        css`
            /* Relative host so the panel can be absolutely positioned above the chip — plain
               position:absolute, not CSS anchor-positioning (position-area is unreliable in the VS
               WebView2's Chromium: the top-layer popover jumps to the viewport corner). */
            :host {
                position: relative;
                display: inline-flex;
            }
            /* Chip is a <fluent-button> — keep it pure (only layout). */
            .chip {
                display: inline-flex;
                align-items: center;
                /* Sit apart from the neighbouring toolbar buttons (+, lightning). */
                margin-inline-start: 4px;
            }
            .chip svg {
                width: 16px;
                height: 16px;
            }
            /* Wrap the icon so the count badge can overlay its top-right corner (notification
               style), instead of sitting in the button flow. */
            .icon-wrap {
                position: relative;
                display: inline-flex;
            }
            .count {
                position: absolute;
                top: -7px;
                right: -9px;
            }

            /* Click panel: absolutely positioned above the chip, left-aligned to it. */
            .popover {
                position: absolute;
                bottom: calc(100% + 4px);
                left: 0;
                z-index: 1000;
                padding: 8px 10px;
                width: 420px;
                max-width: 90vw;
                font-size: var(--fontSizeBase300);
                background: var(--colorNeutralBackground1);
                color: var(--colorNeutralForeground1);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                box-shadow: var(--shadow16);
            }
            .popover[hidden] {
                display: none;
            }
            .head {
                display: flex;
                align-items: center;
                justify-content: space-between;
                font-size: 1.1em;
                font-weight: var(--fontWeightSemibold);
                margin-bottom: 10px;
            }
            .row {
                display: flex;
                align-items: center;
                gap: 8px;
                padding: 6px 0;
                border-bottom: 1px solid var(--colorNeutralStroke3);
            }
            .main {
                flex: 1;
                min-width: 0;
            }
            .desc {
                color: var(--colorNeutralForeground1);
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .meta {
                font-size: 0.82em;
                color: var(--colorNeutralForeground3);
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            /* What it's doing right now, ahead of the running totals: with several near-identical
               agents this is the only part that tells them apart. */
            .meta .now {
                color: var(--colorNeutralForeground2);
            }
            .meta .now::after {
                content: ' ·';
                color: var(--colorNeutralForeground3);
            }
            .time {
                flex-shrink: 0;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
                font-variant-numeric: tabular-nums;
            }
            /* Stop / Stop-all are <fluent-button> — keep them pure; only layout here. */
            .stopall,
            .stop {
                flex-shrink: 0;
            }
            /* Same stop glyph as the rows, next to the label: the icon alone is ambiguous, so
               pairing it with the word here teaches what the per-row squares mean. Spacing goes
               on the svg — Fluent's own part styles win over a gap set on ::part(control). */
            .stopall svg {
                width: 16px;
                height: 16px;
                margin-inline-end: 6px;
                vertical-align: -3px;
                color: var(--colorStatusDangerForeground1, #d13438);
            }
            /* Per-agent Stop: red, like the Stop-all glyph. Neutral grey reads as disabled and
               the square stops looking clickable at all. Coloring the icon (not the button)
               keeps the Fluent component pure. */
            .stop svg {
                width: 16px;
                height: 16px;
                display: block;
                color: var(--colorStatusDangerForeground1, #d13438);
            }
        `,
    ];

    @property({ attribute: false }) tasks: SubagentTask[] = [];

    @state() private _open = false;

    override connectedCallback(): void {
        super.connectedCallback();
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
    }

    // Light dismiss: a click outside the chip closes the panel (composedPath crosses the shadow).
    private _onDocPointerDown = (e: PointerEvent): void => {
        if (this._open && !e.composedPath().includes(this)) {
            this._open = false;
        }
    };

    /** The CLI prefixes the description with its own status verb ("Running Run build…"), which
     *  the live dot already conveys — strip it so the row reads as the task itself. */
    private _desc(t: SubagentTask): string {
        const d = (t.description || '').trim();
        return d.replace(/^running\s+/i, '') || 'sub-agent';
    }

    private _fmt(ms: number): string {
        const s = Math.round(ms / 1000);
        return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
    }
    private _stop(taskId: string) {
        bridge.sendNotification<SubagentCancelNotification>(Msg.fromWebView.chat.subagentCancel, {
            taskId,
        });
    }
    private _stopAll() {
        for (const t of this.tasks) {
            bridge.sendNotification<SubagentCancelNotification>(
                Msg.fromWebView.chat.subagentCancel,
                { taskId: t.taskId },
            );
        }
    }

    private _toggle = (): void => {
        this._open = !this._open;
    };

    override render() {
        const n = this.tasks.length;
        if (n === 0) {
            return nothing;
        }
        return html`<fluent-button
                id="cv-subagent-chip-btn"
                class="chip"
                appearance="subtle"
                size="small"
                icon-only
                aria-label=${`${n} active sub-agent${n === 1 ? '' : 's'}`}
                @click=${this._toggle}
            >
                <span class="icon-wrap">
                    ${unsafeHTML(Bot16Regular)}
                    <fluent-counter-badge
                        class="count"
                        count=${n}
                        size="small"
                    ></fluent-counter-badge>
                </span>
            </fluent-button>
            <div id="cv-subagents-popover" class="popover" ?hidden=${!this._open}>
                <div class="head">
                    <span>Sub-agents (${n})</span>
                    <fluent-button
                        class="stopall"
                        appearance="subtle"
                        size="small"
                        @click=${this._stopAll}
                    >
                        ${unsafeHTML(Stop16Filled)}
                        <span>Stop all</span>
                    </fluent-button>
                </div>
                ${this.tasks.map(
                    (t) =>
                        html`<div class="row">
                            <span class="cv-dot active"></span>
                            <div class="main">
                                <div class="desc">${this._desc(t)}</div>
                                <div class="meta">
                                    <span class="now"
                                        >${t.recentTools[t.recentTools.length - 1] ?? '—'}</span
                                    >
                                    ${t.usage.toolUses} ${t.usage.toolUses === 1 ? 'tool' : 'tools'}
                                    · ${formatTokens(t.usage.totalTokens)} tok
                                </div>
                            </div>
                            <span class="time">${this._fmt(t.usage.durationMs)}</span>
                            <fluent-button
                                class="stop"
                                appearance="transparent"
                                size="small"
                                title="Stop"
                                aria-label="Stop"
                                @click=${() => this._stop(t.taskId)}
                                >${unsafeHTML(Stop16Filled)}</fluent-button
                            >
                        </div>`,
                )}
            </div>`;
    }
}
declare global {
    interface HTMLElementTagNameMap {
        'cv-subagent-chip': CvSubagentChip;
    }
}

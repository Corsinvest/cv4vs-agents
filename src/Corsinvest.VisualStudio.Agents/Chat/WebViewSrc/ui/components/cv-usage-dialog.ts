/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing, type TemplateResult } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { dialogStyles, iconStyles } from '../styles/shared';
import { bridge } from '../../core/bridge';
import { GetUsageReq } from '../../core/request-types';
import type { UsageDto, RateWindowDto, UsageAttributionDto } from '../../core/types';
import { CvDialogBase } from './cv-dialog-base';

/** Relative "Resets in 3h / 4d" from an ISO timestamp. Coarse on purpose. */
function resetsIn(iso: string | null, nowMs: number): string {
    if (!iso) {
        return '';
    }
    const ms = Date.parse(iso) - nowMs;
    if (!Number.isFinite(ms) || ms <= 0) {
        return 'Resets soon';
    }
    const h = Math.round(ms / 3_600_000);
    if (h < 24) {
        return `Resets in ${Math.max(1, h)}h`;
    }
    return `Resets in ${Math.round(h / 24)}d`;
}

/**
 * Account & Usage modal dialog (the `/usage` command). Parent-controlled via
 * `open`; on open it requests `get_chat_usage` and renders the response (account
 * info from init + the CLI's experimental usage windows). Uses fluent-dialog
 * (backdrop + focus-trap + Esc for free); `open` drives show()/hide().
 */
@customElement('cv-usage-dialog')
export class CvUsageDialog extends CvDialogBase {
    static override styles = [
        dialogStyles,
        iconStyles,
        css`
            .section {
                margin-top: 14px;
                margin-bottom: 4px;
                text-transform: uppercase;
                letter-spacing: 0.04em;
                font-size: 0.85em;
                font-weight: var(--fontWeightSemibold);
                color: var(--colorNeutralForeground3);
            }
            .kv {
                display: flex;
                justify-content: space-between;
                gap: 16px;
                padding: 6px 0;
                border-bottom: 1px solid var(--colorNeutralStroke3);
            }
            .kv > span:first-child {
                color: var(--colorNeutralForeground2);
                flex-shrink: 0;
            }
            .kv > span:last-child {
                text-align: right;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .row {
                padding: 8px 0 4px;
            }
            .row-head {
                display: flex;
                justify-content: space-between;
                margin-bottom: 6px;
            }
            .pct {
                font-variant-numeric: tabular-nums;
                color: var(--colorNeutralForeground2);
            }
            /* fluent-progress-bar stays pure — only vertical spacing in the row. */
            .bar {
                display: block;
                margin: 4px 0;
            }
            .reset {
                margin-top: 4px;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
            }
            /* fluent-link stays pure — only top spacing separating it from the bars above. */
            .link {
                display: inline-block;
                margin-top: 16px;
            }
            /* fluent-tablist stays pure — only spacing around the Day/Week tabs. */
            .period {
                margin: 8px 0 4px;
            }
            .note {
                margin-top: 6px;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
            }
            .insight {
                margin-top: 14px;
            }
            .insight-head {
                font-weight: var(--fontWeightSemibold);
                margin-bottom: 4px;
            }
            .insight-body {
                font-size: 0.9em;
                color: var(--colorNeutralForeground3);
                line-height: 1.4;
            }
            .attr {
                margin-top: 8px;
            }
            .attr-head {
                display: flex;
                justify-content: space-between;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
                margin-bottom: 2px;
            }
            .attr-row {
                display: flex;
                justify-content: space-between;
                padding: 2px 0;
            }
        `,
    ];

    @state() private _data: UsageDto | null = null;
    @state() private _loading = false;
    @state() private _period: 'day' | 'week' = 'day';
    // Timestamp captured when the payload arrives, for "Resets in …" (Date.now
    // is fine here — display only, not persisted).
    private _nowMs = 0;

    override willUpdate(changed: Map<string, unknown>): void {
        if (changed.has('open') && this.open) {
            // Fresh fetch each time it opens. Correlated request: each open gets its own
            // response (no more last-response-wins race across dialogs).
            this._data = null;
            this._loading = true;
            bridge
                .sendRequest(GetUsageReq, {})
                .then((d) => {
                    this._data = (d as UsageDto) ?? null;
                    this._nowMs = Date.now();
                    this._loading = false;
                })
                .catch(() => {
                    this._loading = false;
                }); // never leave the spinner stuck
        }
    }

    /** A usage bar — the C# DTO already carries the label + clamped utilization. */
    private _bar(w: RateWindowDto): TemplateResult {
        return html`
            <div class="row">
                <div class="row-head">
                    <span>${w.name}</span>
                    <span class="pct">${w.utilization}%</span>
                </div>
                <fluent-progress-bar class="bar" value=${w.utilization}></fluent-progress-bar>
                <div class="reset">${resetsIn(w.resetsAt, this._nowMs)}</div>
            </div>
        `;
    }

    /** One attribution group (Skills / Subagents / Plugins / MCP servers). */
    private _attribution(
        title: string,
        items: UsageAttributionDto[] | null | undefined,
    ): TemplateResult | typeof nothing {
        if (!items || items.length === 0) {
            return nothing;
        }
        return html`
            <div class="attr">
                <div class="attr-head">
                    <span>${title}</span>
                    <span class="pct">% of usage</span>
                </div>
                ${items.map(
                    (it) => html`
                        <div class="attr-row">
                            <span>${it.name}</span>
                            <span class="pct">${it.pct}%</span>
                        </div>
                    `,
                )}
            </div>
        `;
    }

    // The active tab id lives on tablist.activetab; read its id on change (day/week).
    private _onPeriodChange = (e: Event): void => {
        const id = (e.target as HTMLElement & { activetab?: HTMLElement }).activetab?.id;
        if (id === 'day' || id === 'week') {
            this._period = id;
        }
    };

    /** "What's contributing to your limits usage?" — the C# DTO already carries the day/week
     *  behaviours (insights filtered, attribution grouped). */
    private _renderBehaviors(): TemplateResult | typeof nothing {
        const period = this._period === 'day' ? this._data?.day : this._data?.week;
        if (!period) {
            return nothing;
        }
        const periodLabel = this._period === 'day' ? 'Last 24h' : 'Last 7 days';
        return html`
            <div class="section">What's contributing to your limits usage?</div>
            <fluent-tablist
                class="period"
                size="small"
                activeid=${this._period}
                @change=${this._onPeriodChange}
            >
                <fluent-tab id="day">Day</fluent-tab>
                <fluent-tab id="week">Week</fluent-tab>
            </fluent-tablist>
            <div class="note">
                Approximate, based on local sessions on this machine — does not include other
                devices or claude.ai
            </div>
            <div class="note">
                ${periodLabel} · these are independent characteristics of your usage, not a
                breakdown
            </div>
            ${(period.insights ?? []).map(
                (x) => html`
                    <div class="insight">
                        <div class="insight-head">${x.headline}</div>
                        <div class="insight-body">${x.body}</div>
                    </div>
                `,
            )}
            <div class="insight">
                <div class="insight-head">Skills, subagents, plugins, and MCP servers</div>
                ${
                    period.hasAttribution
                        ? html`
                              ${this._attribution('Skills', period.skills)}
                              ${this._attribution('Subagents', period.subagents)}
                              ${this._attribution('Plugins', period.plugins)}
                              ${this._attribution('MCP servers', period.mcpServers)}
                          `
                        : html`<div class="insight-body">
                              No attribution data yet · accumulates as you use Claude
                          </div>`
                }
            </div>
        `;
    }

    private _renderBody(): TemplateResult {
        if (this._loading || !this._data) {
            return html`<div class="cv-dialog-loading">Loading…</div>`;
        }
        const d = this._data;
        const acct = d.account;
        return html`
            <div class="section">Account</div>
            <div class="kv"><span>Auth method</span><span>${d.authMethod}</span></div>
            ${
                acct?.email
                    ? html`<div class="kv"><span>Email</span><span>${acct.email}</span></div>`
                    : nothing
            }
            ${
                acct?.organization
                    ? html`<div class="kv">
                          <span>Organization</span><span>${acct.organization}</span>
                      </div>`
                    : nothing
            }
            <div class="kv"><span>Plan</span><span>${d.plan}</span></div>

            ${
                d.rateLimitsAvailable && d.windows.length > 0
                    ? html`
                          <div class="section">Usage</div>
                          ${d.windows.map((w) => this._bar(w))}
                      `
                    : html`<div class="section">Usage</div>
                          <div class="cv-dialog-loading">
                              Plan usage is not available for this session.
                          </div>`
            }
            ${
                // Only for the first-party Claude AI account (undefined = native Claude); for 3rd-party
                // providers (z.ai/GLM, Bedrock, Vertex, gateway) the claude.ai link points nowhere useful.
                !acct?.apiProvider || acct.apiProvider === 'firstParty'
                    ? html`<fluent-link
                          class="link"
                          href="https://claude.ai/settings/usage"
                          target="_blank"
                          >Manage usage on claude.ai</fluent-link
                      >`
                    : nothing
            }
            ${this._renderBehaviors()}
        `;
    }

    override render() {
        // Create-on-open: nothing in the DOM while closed → the dialog is destroyed on close.
        if (!this.open) {
            return nothing;
        }
        return html`
            <fluent-dialog
                type="modal"
                aria-label="Account & Usage"
                @toggle=${this._onDialogToggle}
            >
                <fluent-dialog-body>
                    <h2 slot="title">Account &amp; Usage</h2>
                    <fluent-button
                        slot="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Close"
                        >${unsafeHTML(Dismiss16Regular)}</fluent-button
                    >
                    ${this._renderBody()}
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-usage-dialog': CvUsageDialog;
    }
}

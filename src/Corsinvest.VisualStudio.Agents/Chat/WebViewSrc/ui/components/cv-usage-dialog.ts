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
import type { AccountDto } from '../../core/generated/AccountDto';
import { CvDialogBase } from './cv-dialog-base';

interface RateWindow {
    utilization: number | null;
    resets_at: string | null;
}
interface BehaviorItem {
    key: string;
    pct: number;
    count: number;
}
interface AttributionItem {
    name?: string;
    pct?: number;
}
interface BehaviorPeriod {
    behaviors?: BehaviorItem[] | null;
    skills?: AttributionItem[] | null;
    subagents?: AttributionItem[] | null;
    agents?: AttributionItem[] | null;
    plugins?: AttributionItem[] | null;
    mcp_servers?: AttributionItem[] | null;
}
interface UsagePayload {
    account: AccountDto | null;
    usage: {
        subscription_type?: string | null;
        rate_limits_available?: boolean;
        rate_limits?: Record<string, RateWindow | null> | null;
        session?: { total_cost_usd?: number } | null;
        behaviors?: { day?: BehaviorPeriod | null; week?: BehaviorPeriod | null } | null;
    } | null;
}

/** key → (headline given pct) + body. Mirrors the VS Code extension's insight copy. */
const BEHAVIOR_COPY: Record<string, { headline: (pct: number) => string; body: string }> = {
    long_context: {
        headline: (pct) => `${pct}% of your usage was at >150k context`,
        body: 'Longer sessions are more expensive even when cached. /compact mid-task, /clear when switching to new tasks.',
    },
    subagent_heavy: {
        headline: (pct) => `${pct}% of your usage came from subagent-heavy sessions`,
        body: 'Each subagent runs its own requests. Be deliberate about spawning them — and consider configuring a cheaper model for simpler subagents.',
    },
};

/** Human label for the auth backend (apiProvider). */
function authLabel(p?: string): string {
    switch (p) {
        case 'firstParty':
            return 'Claude AI';
        case 'bedrock':
            return 'Amazon Bedrock';
        case 'vertex':
            return 'Google Vertex';
        case 'gateway':
            return 'Enterprise gateway';
        default:
            return p ? p : 'API key';
    }
}

/** "Claude max" from "max", "Claude pro" from "pro", etc. */
function planLabel(sub?: string | null): string {
    return sub ? `Claude ${sub}` : '—';
}

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

    @state() private _data: UsagePayload | null = null;
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
                    this._data = (d as UsagePayload) ?? null;
                    this._nowMs = Date.now();
                    this._loading = false;
                })
                .catch(() => {
                    this._loading = false;
                }); // never leave the spinner stuck
        }
    }

    /** A labelled usage bar (Session / Weekly / per-model). */
    private _bar(label: string, w: RateWindow | null | undefined): TemplateResult | typeof nothing {
        if (!w) {
            return nothing;
        }
        const pct = Math.max(0, Math.min(100, Math.round(w.utilization ?? 0)));
        return html`
            <div class="row">
                <div class="row-head">
                    <span>${label}</span>
                    <span class="pct">${pct}%</span>
                </div>
                <fluent-progress-bar class="bar" value=${pct}></fluent-progress-bar>
                <div class="reset">${resetsIn(w.resets_at, this._nowMs)}</div>
            </div>
        `;
    }

    /** One attribution group (Skills / Subagents / Plugins / MCP servers). */
    private _attribution(
        title: string,
        items: AttributionItem[] | null | undefined,
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
                            <span>${it.name ?? '—'}</span>
                            <span class="pct">${Math.round(it.pct ?? 0)}%</span>
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

    /** "What's contributing to your limits usage?" — from `usage.behaviors`. */
    private _renderBehaviors(): TemplateResult | typeof nothing {
        const b = this._data?.usage?.behaviors;
        const period = b?.[this._period];
        if (!b || !period) {
            return nothing;
        }
        const insights = (period.behaviors ?? []).filter((x) => BEHAVIOR_COPY[x.key]);
        const hasAttribution =
            (period.skills?.length ?? 0) +
                (period.subagents?.length ?? 0) +
                (period.agents?.length ?? 0) +
                (period.plugins?.length ?? 0) +
                (period.mcp_servers?.length ?? 0) >
            0;
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
            ${insights.map((x) => {
                const copy = BEHAVIOR_COPY[x.key];
                return html`
                    <div class="insight">
                        <div class="insight-head">${copy.headline(Math.round(x.pct ?? 0))}</div>
                        <div class="insight-body">${copy.body}</div>
                    </div>
                `;
            })}
            <div class="insight">
                <div class="insight-head">Skills, subagents, plugins, and MCP servers</div>
                ${
                    hasAttribution
                        ? html`
                              ${this._attribution('Skills', period.skills)}
                              ${this._attribution('Subagents', period.subagents ?? period.agents)}
                              ${this._attribution('Plugins', period.plugins)}
                              ${this._attribution('MCP servers', period.mcp_servers)}
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
        const acct = this._data.account;
        const usage = this._data.usage;
        const rl = usage?.rate_limits ?? null;
        const sub = usage?.subscription_type ?? acct?.subscriptionType;
        return html`
            <div class="section">Account</div>
            <div class="kv">
                <span>Auth method</span><span>${authLabel(acct?.apiProvider)}</span>
            </div>
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
            <div class="kv"><span>Plan</span><span>${planLabel(sub)}</span></div>

            ${
                rl && usage?.rate_limits_available !== false
                    ? html`
                          <div class="section">Usage</div>
                          ${this._bar('Session (5hr)', rl.five_hour)}
                          ${this._bar('Weekly (7 day)', rl.seven_day)}
                          ${this._bar('Weekly Opus', rl.seven_day_opus)}
                          ${this._bar('Weekly Sonnet', rl.seven_day_sonnet)}
                      `
                    : html`<div class="section">Usage</div>
                          <div class="cv-dialog-loading">
                              Plan usage is not available for this session.
                          </div>`
            }

            <fluent-link class="link" href="https://claude.ai/settings/usage" target="_blank"
                >Manage usage on claude.ai</fluent-link
            >

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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing, type TemplateResult } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { keyed } from 'lit/directives/keyed.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { dialogStyles, iconStyles } from '../styles/shared';
import './cv-segmented';
import { fetchStats } from '../../core/lazy';
import { formatTokens } from '../../core/ai-models';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import type { StatsResponse, StatsScopeDto, StatsRangeDto } from '../../core/types';
import './cv-heatmap';
import type { HeatmapCell } from './cv-heatmap';
import { CvDialogBase } from './cv-dialog-base';

// Combo option lists — value is the wire DTO union, label the UI string.
// Short labels for the segmented control; `title` carries the full wording as a tooltip.
const SCOPES: ReadonlyArray<{ value: StatsScopeDto; label: string; title: string }> = [
    { value: 'all', label: 'All', title: 'All chats' },
    { value: 'project', label: 'Project', title: 'This project' },
    { value: 'session', label: 'Current', title: 'Current chat' },
];
// Short labels for the segmented control; `title` carries the full wording as a tooltip.
const RANGES: ReadonlyArray<{ value: StatsRangeDto; label: string; title: string }> = [
    { value: 'all', label: 'All', title: 'All time' },
    { value: 'days30', label: '30d', title: 'Last 30 days' },
    { value: 'days7', label: '7d', title: 'Last 7 days' },
];

type Tab = 'overview' | 'models';

// Stable color per model, keyed by its index in the (token-sorted) breakdown. Used by both the
// stacked bar chart and the per-model bars so a model has ONE color everywhere. Theme-aware.
const MODEL_COLORS = [
    'var(--colorPaletteBlueBorderActive)',
    'var(--colorPaletteLavenderBorderActive)',
    'var(--colorPaletteTealBorderActive)',
    'var(--colorPaletteGreenBorderActive)',
    'var(--colorPaletteMarigoldBorderActive)',
    'var(--colorPaletteDarkOrangeBorderActive)',
    'var(--colorPaletteBerryBorderActive)',
];
const modelColor = (i: number): string => MODEL_COLORS[i % MODEL_COLORS.length];

/** One model's slice of a day's tokens (for the hover card): name, tokens, its palette colour. */
interface DayRow {
    name: string;
    tok: number;
    color?: string;
}

/** Everything one day's hover card needs — shared by the heatmap and the chart so both cards are
 *  identical: activity counts + the per-model token rows. */
interface DayInfo {
    date: string;
    messages: number;
    sessions: number;
    tools: number;
    rows: DayRow[];
}

// The smallest "nice" step >= target: cycles 1 → 2 → 5 → 10 → 20 → 50 → … (the 1/2/5 sequence,
// magnitude climbing on its own), so it scales to any size with no hardcoded ceiling. Shared logic
// with the WPF chart (CvBarChart.NiceStep).
const niceStep = (target: number): number => {
    const cycle = [1, 2, 5];
    let step = 1;
    let i = 0;
    while (step < target) {
        step = cycle[i % 3] * Math.pow(10, Math.floor(i / 3));
        i++;
    }
    return step;
};

// The axis ceiling: pick a nice tick step (~ max/TICKS), then round the top up to a whole number of
// those steps, so every gridline lands on a round value.
const niceCeil = (max: number, ticks: number): number => {
    if (max <= 0) {
        return 1;
    }
    const step = niceStep(max / ticks);
    return Math.ceil(max / step) * step;
};

// Y-axis tick label: like formatTokens but always 1 decimal above 1M (matches the "12.0M" look).
const formatAxis = (n: number): string => {
    if (n >= 1_000_000_000) {
        return +(n / 1_000_000_000).toFixed(1) + 'B';
    }
    if (n >= 1_000_000) {
        return +(n / 1_000_000).toFixed(1) + 'M';
    }
    if (n >= 1_000) {
        return Math.round(n / 1_000) + 'k';
    }
    return String(Math.round(n));
};

// X-axis date label: short day+month in the UI locale (e.g. "12 dic"). Input is "yyyy-mm-dd"
// (or the Monday of an ISO week when bucketed weekly) — parse as local, not UTC.
const formatDay = (iso: string): string => {
    const [y, m, dd] = iso.split('-').map(Number);
    if (!y || !m || !dd) {
        return iso;
    }
    return new Date(y, m - 1, dd).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
};

// One heatmap cell: the date, its message count, and an intensity 0–4 (0 = no activity).
interface HeatCell {
    date: string;
    count: number;
    intensity: number;
    future: boolean;
}

/** GitHub-style activity heatmap: 7 rows (Sun→Sat) × N week columns, ending on the current week.
 *  Intensity is bucketed by percentiles of the active days' message counts (0 = none, 1–4 rising).
 *  Mirrors the CLI's heatmap.ts (p25/p50/p75 → levels). Returns columns of 7 cells each. */
function buildHeatmap(
    activity: ReadonlyArray<{ date: string; messageCount: number }>,
): HeatCell[][] {
    const byDate = new Map(activity.map((a) => [a.date, a.messageCount]));
    // Percentiles over ACTIVE days only (sorted ascending), like the CLI.
    const counts = activity
        .map((a) => a.messageCount)
        .filter((c) => c > 0)
        .sort((a, b) => a - b);
    const at = (q: number): number =>
        counts.length ? (counts[Math.floor(counts.length * q)] ?? 0) : 0;
    const p25 = at(0.25),
        p50 = at(0.5),
        p75 = at(0.75);
    const intensityOf = (c: number): number => {
        if (c === 0 || counts.length === 0) {
            return 0;
        }
        if (c >= p75) {
            return 4;
        }
        if (c >= p50) {
            return 3;
        }
        if (c >= p25) {
            return 2;
        }
        return 1;
    };

    // Local yyyy-MM-dd (matches the host's DateKey, which is local time).
    const key = (d: Date): string =>
        `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

    // How many weeks to show: from the earliest active day to today, capped at 52.
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    let earliest = today;
    for (const a of activity) {
        const [y, m, dd] = a.date.split('-').map(Number);
        const d = new Date(y, m - 1, dd);
        if (d < earliest) {
            earliest = d;
        }
    }
    const weekStart = new Date(today);
    weekStart.setDate(today.getDate() - today.getDay()); // Sunday of current week
    const spanWeeks = Math.ceil((weekStart.getTime() - earliest.getTime()) / (7 * 86_400_000)) + 1;
    const weeks = Math.min(52, Math.max(1, spanWeeks));

    const start = new Date(weekStart);
    start.setDate(start.getDate() - (weeks - 1) * 7);

    const cols: HeatCell[][] = [];
    const cur = new Date(start);
    for (let w = 0; w < weeks; w++) {
        const col: HeatCell[] = [];
        for (let day = 0; day < 7; day++) {
            const ds = key(cur);
            const future = cur > today;
            const count = future ? 0 : (byDate.get(ds) ?? 0);
            col.push({ date: ds, count, intensity: future ? 0 : intensityOf(count), future });
            cur.setDate(cur.getDate() + 1);
        }
        cols.push(col);
    }
    return cols;
}

/**
 * Statistics dialog: historical usage aggregated from the local session .jsonl
 * (tokens, sessions, messages, active days, streaks, per-model breakdown, heatmap).
 * Two combos — scope (all/project/session) + range (all/30d/7d) — refetch on change.
 * Ours, not the CLI's; distinct from /usage (plan) and /context (current window).
 * First "all" aggregation is heavy; the host caches per-project after.
 *
 * Uses fluent-dialog (backdrop + focus-trap + Esc close for free). The `open`
 * property drives it imperatively: reflected to show()/hide() in `updated`.
 */
@customElement('cv-stats-dialog')
export class CvStatsDialog extends CvDialogBase {
    static override styles = [
        dialogStyles,
        iconStyles,
        css`
            /* Widen past Fluent's 600px default — the card grid + heatmap need room. */
            fluent-dialog::part(dialog) {
                width: 620px;
                max-width: 92vw;
            }
            /* Single indeterminate loading bar under the tabs. Always laid out (keeps its
             * ~2px row so the grid never shifts); hidden — not removed — when idle. */
            .loading {
                display: block;
                margin-bottom: 8px;
            }
            .loading.idle {
                visibility: hidden;
            }
            /* Combos row (scope + range). */
            .combos {
                display: flex;
                justify-content: space-between;
                gap: 8px;
                margin-bottom: 12px;
            }
            /* Tabs (Overview / Models) — fluent-tablist stays pure; only spacing below it. */
            .tabs {
                margin-bottom: 12px;
            }
            /* Overview card grid — 4 columns (16 cards → 4×4), matching the WPF Statistics tab. */
            .grid {
                display: grid;
                grid-template-columns: repeat(4, 1fr);
                gap: 8px;
            }
            .card {
                padding: 10px 12px;
                background: var(--colorNeutralBackground2);
                border: 1px solid var(--colorNeutralStroke2);
                border-radius: var(--borderRadiusMedium);
            }
            .card-value {
                font-size: 1.35em;
                font-weight: var(--fontWeightSemibold);
                line-height: 1.2;
            }
            .card-label {
                margin-top: 2px;
                color: var(--colorNeutralForeground3);
                font-size: var(--fontSizeBase200);
            }
            /* Stacked bar chart: tokens per period, one segment per model. Bars grow from the
             * baseline; each is a bottom-up flex column of colored segments by token share.
             * Grid layout: [Y-axis | plot] on top, [pad | X-axis] below — so the Y labels sit
             * left of the bars and the date labels sit under them, aligned to each bar column. */
            .chart {
                margin-bottom: 14px;
                display: grid;
                grid-template-columns: auto 1fr;
                grid-template-rows: auto auto;
                column-gap: 6px;
            }
            /* Y axis: tick labels top→bottom (rendered reversed so niceMax is on top, 0 at bottom). */
            .chart-yaxis {
                display: flex;
                flex-direction: column;
                justify-content: space-between;
                height: 90px;
                text-align: right;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                line-height: 1;
            }
            .chart-ytick {
                display: block;
            }
            /* Plot: gridlines layer behind the bars. */
            .chart-plot {
                position: relative;
                height: 90px;
            }
            .chart-grid {
                position: absolute;
                inset: 0;
                display: flex;
                flex-direction: column;
                justify-content: space-between;
                pointer-events: none;
            }
            .chart-gridline {
                display: block;
                height: 1px;
                background: var(--colorNeutralStroke2);
                opacity: 0.5;
            }
            .chart-bars {
                position: relative;
                display: flex;
                align-items: flex-end;
                gap: 2px;
                height: 100%;
                overflow-x: auto;
            }
            /* X axis: one slot per bar, aligned under the bars; only some carry a (spaced) label. */
            .chart-xaxis {
                display: flex;
                gap: 2px;
                margin-top: 3px;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                overflow: hidden;
            }
            .chart-xtick {
                flex: 1 1 auto;
                min-width: 4px;
                white-space: nowrap;
                overflow: visible;
            }
            /* Bar tooltip (fluent-tooltip): the card content comes from _dayTip with inline styles
             * (it also renders inside cv-heatmap's shadow, so it can't rely on classes here). DON'T
             * set display on the fluent-tooltip host — it toggles none↔visible for show/hide. */
            fluent-tooltip {
                padding: 6px 8px;
            }
            .chart-bar {
                flex: 1 1 auto;
                min-width: 4px;
                display: flex;
                flex-direction: column-reverse; /* segments stack from the bottom */
                border-radius: 2px 2px 0 0;
                overflow: hidden;
                cursor: pointer;
                /* outline (not border) so the highlight adds no layout width → bars don't shift. */
                outline: 1px solid transparent;
                outline-offset: 1px;
                transition:
                    outline-color 0.12s ease,
                    filter 0.12s ease;
            }
            .chart-bar:hover {
                outline-color: var(--colorNeutralStroke1);
                filter: brightness(
                    1.12
                ); /* pop the hovered bar's segments without changing their hues */
            }
            .chart-seg {
                width: 100%;
                flex-grow: 0;
                flex-shrink: 0;
                flex-basis: 0;
            }
            /* Small color dot before a model name, matching its chart color. */
            .model-dot {
                width: 10px;
                height: 10px;
                border-radius: 2px;
                flex: 0 0 auto;
            }
            /* Per-model breakdown: name + % on top, share bar, in/out tokens below. */
            .models {
                display: flex;
                flex-direction: column;
                gap: 12px;
            }
            .model-head {
                display: flex;
                align-items: center;
                gap: 8px;
            }
            .model-name {
                flex: 1;
                font-weight: var(--fontWeightSemibold);
                overflow-wrap: anywhere;
            }
            .model-pct {
                color: var(--colorNeutralForeground2);
                font-variant-numeric: tabular-nums;
            }
            /* fluent-progress-bar stays pure — only vertical spacing around the share bar. */
            .model-bar {
                display: block;
                margin: 4px 0;
            }
            .model-tok {
                color: var(--colorNeutralForeground3);
                font-size: var(--fontSizeBase200);
            }
            /* Activity heatmap (cv-heatmap) sits below the card grid. */
            .heat {
                display: block;
                margin-top: 14px;
            }
            /* Loading / empty placeholder. */
            .status,
            .empty {
                padding: 20px 0;
                text-align: center;
                color: var(--colorNeutralForeground3);
            }
        `,
    ];

    @state() private _data: StatsResponse | null = null;
    @state() private _error = false;
    // Default to the current project: fast (one folder vs all) and the most relevant
    // when opening stats from the project you're working in. "All" is one combo click away.
    @state() private _scope: StatsScopeDto = 'project';
    @state() private _range: StatsRangeDto = 'all';
    @state() private _tab: Tab = 'overview';

    // Unsubscribe for the stats_index_done listener (bridge.onNotification returns a disposer).
    private _offIndexDone?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        // Create-on-open: the element is added when the dialog opens, so this runs once per open.
        // Kick the background index (host-side single-flight → no-op if already running).
        bridge.sendNotification(Msg.fromWebView.chat.startStatsIndex, {});
        // …and re-read (keeping the current numbers, no flicker) each time a pass finishes, so the
        // partial figures fill in and the loading bar clears.
        this._offIndexDone = bridge.onNotification(Msg.toWebView.chat.statsIndexDone, () => {
            if (this.open) {
                this._load({ clear: false });
            }
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offIndexDone?.();
        this._offIndexDone = undefined;
    }

    override willUpdate(changed: Map<string, unknown>): void {
        // Refetch on open and whenever a combo changes. Only the very first open clears to "—"
        // (no data yet); a combo change keeps the previous numbers on screen and swaps them in
        // place when the new ones arrive, so the layout never collapses/flickers — the loading
        // bar signals the refresh instead.
        const firstOpen = changed.has('open') && this.open;
        if (firstOpen || changed.has('_scope') || changed.has('_range')) {
            if (this.open) {
                this._load({ clear: firstOpen });
            }
        }
    }

    // Bumped on every _load so a stale response (combo changed, or dialog reopened) is
    // ignored. The bridge request itself keeps running host-side and still populates the
    // cache — so closing the dialog mid-aggregation isn't wasted work, just not shown.
    private _reqSeq = 0;

    // `clear` blanks the grid to "—" first (combo change / initial open — the old data is for a
    // different scope). A background-index refresh passes clear:false so the current numbers stay
    // put and just update in place → no flicker while re-reading on each stats_index_done.
    private _load({ clear }: { clear: boolean }): void {
        if (clear) {
            this._data = null;
        }
        this._error = false;
        const seq = ++this._reqSeq;
        fetchStats(this._scope, this._range)
            .then((d) => {
                if (seq === this._reqSeq && this.isConnected) {
                    this._data = d;
                }
            })
            .catch(() => {
                if (seq === this._reqSeq && this.isConnected) {
                    this._error = true;
                }
            });
    }

    // Loading bar visible while: the first response hasn't arrived, or a background index pass
    // is still running (numbers are partial). Cleared once we have complete, non-indexing data.
    private get _busy(): boolean {
        return !this._error && (!this._data || this._data.indexing);
    }

    private _renderCombos(): TemplateResult {
        return html`
            <div class="combos">
                <cv-segmented
                    .options=${SCOPES}
                    .activeValue=${this._scope}
                    @change=${(e: CustomEvent<{ value: StatsScopeDto }>) => {
                        this._scope = e.detail.value;
                    }}
                ></cv-segmented>
                <cv-segmented
                    .options=${RANGES}
                    .activeValue=${this._range}
                    @change=${(e: CustomEvent<{ value: StatsRangeDto }>) => {
                        this._range = e.detail.value;
                    }}
                ></cv-segmented>
            </div>
        `;
    }

    // The active tab id lives on tablist.activetab (an element); read its id on change.
    private _onTabChange = (e: Event): void => {
        const active = (e.target as HTMLElement & { activetab?: HTMLElement }).activetab;
        const id = active?.id as Tab | undefined;
        if (id && id !== this._tab) {
            this._tab = id;
        }
    };

    private _renderTabs(): TemplateResult {
        return html`
            <fluent-tablist
                class="tabs"
                size="small"
                activeid=${this._tab}
                @change=${this._onTabChange}
            >
                <fluent-tab id="overview">Overview</fluent-tab>
                <fluent-tab id="models">Models</fluent-tab>
            </fluent-tablist>
        `;
    }

    private _stat(label: string, value: string): TemplateResult {
        return html`
            <div class="card">
                <div class="card-value">${value}</div>
                <div class="card-label">${label}</div>
            </div>
        `;
    }

    // Overview grid. `d` is null while (re)loading — the layout stays put and every value
    // shows a "—" placeholder, so changing a combo never resizes the dialog.
    private _renderOverview(d: StatsResponse | null): TemplateResult {
        const dash = '—';
        const peak = d && d.peakHour >= 0 ? `${String(d.peakHour).padStart(2, '0')}:00` : dash;
        const num = (n: number | undefined): string =>
            d && n !== undefined ? n.toLocaleString() : dash;
        const tok = (n: number | undefined): string =>
            d && n !== undefined ? formatTokens(n) : dash;
        // Token split + tool-call total, summed from the breakdowns (the top-level DTO has only the
        // combined total). Kept in the same order the WPF Statistics tab uses.
        const models = d?.modelBreakdown ?? [];
        const input = models.reduce((s, m) => s + m.inputTokens, 0);
        const output = models.reduce((s, m) => s + m.outputTokens, 0);
        const cache = models.reduce((s, m) => s + m.cacheReadTokens + m.cacheCreationTokens, 0);
        const toolCalls = (d?.topTools ?? []).reduce((s, t) => s + t.count, 0);
        return html`
            <div class="grid">
                ${this._stat('Total tokens', tok(d?.totalTokens))}
                ${this._stat('Input tokens', d ? tok(input) : dash)}
                ${this._stat('Output tokens', d ? tok(output) : dash)}
                ${this._stat('Cache tokens', d ? tok(cache) : dash)}
                ${this._stat('Sessions', num(d?.totalSessions))}
                ${this._stat('Messages', num(d?.totalMessages))}
                ${this._stat('Tool calls', d ? num(toolCalls) : dash)}
                ${this._stat('Subagents', num(d?.subagentSessions))}
                ${this._stat('Subagent tokens', tok(d?.subagentTokens))}
                ${this._stat('Active days', num(d?.activeDays))}
                ${this._stat('Current streak', d ? `${d.currentStreak}d` : dash)}
                ${this._stat('Longest streak', d ? `${d.longestStreak}d` : dash)}
                ${this._stat('Peak hour', peak)}
                ${this._stat('Favorite model', d?.favoriteModel || dash)}
                ${this._stat('Images', num(d?.imageCount))}
                ${this._stat('Attachments', num(d?.fileCount))}
            </div>
            ${d && d.dailyActivity.length > 0 ? this._renderHeatmap(d) : nothing}
        `;
    }

    /** GitHub-style activity heatmap under the overview cards, via the generic cv-heatmap.
     *  Passes each day's semantic intensity (0–4); cv-heatmap owns the color scale. */
    /** The rich per-day hover card, identical for the heatmap and the chart (mirrors the WPF
     *  StatsTooltip): full date, an activity line (messages · sessions · tools), then one coloured
     *  row per model (dot · name · tokens · share-of-day), largest first.
     *  Styles are INLINE (not classes): this card renders inside two different shadow roots — the
     *  dialog and cv-heatmap — so it can't rely on either host's stylesheet. */
    private _dayTip(info: DayInfo): TemplateResult {
        const total = info.rows.reduce((s, r) => s + r.tok, 0);
        const rows = [...info.rows].sort((a, b) => b.tok - a.tok);
        const dim = 'color:var(--colorNeutralForeground3)';
        return html`
            <div>
                <div style="font-weight:var(--fontWeightSemibold)">${formatDay(info.date)}</div>
                <div style="${dim};font-size:var(--fontSizeBase200);margin-bottom:6px">
                    ${info.messages.toLocaleString()} messages · ${info.sessions.toLocaleString()}
                    sessions · ${info.tools.toLocaleString()} tools
                </div>
                ${rows.map((r) => {
                    const pct = total > 0 ? (r.tok / total) * 100 : 0;
                    return html`
                        <div style="display:flex;align-items:center;gap:6px;white-space:nowrap">
                            <span
                                style="width:10px;height:10px;border-radius:2px;flex:0 0 auto;background:${r.color}"
                            ></span>
                            <span style="flex:1 1 auto">${r.name}</span>
                            <span style="margin-left:12px;${dim}">
                                ${formatTokens(r.tok)} (${pct.toFixed(0)}%)
                            </span>
                        </div>
                    `;
                })}
            </div>
        `;
    }

    /** Per-date info shared by the heatmap and the chart: activity + per-model token rows (in
     *  breakdown/colour order, non-zero). One pass over dailyActivity + dailyModelTokens. */
    private _dayInfos(d: StatsResponse): Map<string, DayInfo> {
        const colorOf = new Map<string, string>();
        d.modelBreakdown.forEach((m, i) => colorOf.set(m.model, modelColor(i)));

        const infos = new Map<string, DayInfo>();
        const get = (date: string): DayInfo => {
            let i = infos.get(date);
            if (!i) {
                i = { date, messages: 0, sessions: 0, tools: 0, rows: [] };
                infos.set(date, i);
            }
            return i;
        };
        for (const a of d.dailyActivity) {
            const i = get(a.date);
            i.messages += a.messageCount;
            i.sessions += a.sessionCount;
            i.tools += a.toolCallCount;
        }
        for (const day of d.dailyModelTokens) {
            const i = get(day.date);
            const tm = (day.tokensByModel ?? {}) as Record<string, number>;
            // Breakdown order so colours line up with the model dots / chart segments.
            for (const m of d.modelBreakdown) {
                const tok = tm[m.model] ?? 0;
                if (tok > 0) {
                    i.rows.push({ name: m.model, tok, color: colorOf.get(m.model) });
                }
            }
        }
        return infos;
    }

    private _renderHeatmap(d: StatsResponse): TemplateResult {
        const cols = buildHeatmap(d.dailyActivity);
        if (cols.length === 0) {
            return html``;
        }
        const infos = this._dayInfos(d);
        const heatCols: HeatmapCell[][] = cols.map((col) =>
            col.map((c) => {
                if (c.future) {
                    return { intensity: 0, empty: true };
                }
                const info = infos.get(c.date) ?? {
                    date: c.date,
                    messages: c.count,
                    sessions: 0,
                    tools: 0,
                    rows: [],
                };
                return { intensity: c.intensity, tip: this._dayTip(info) };
            }),
        );
        return html`
            <cv-heatmap
                class="heat"
                .cols=${heatCols}
                .legend=${{ less: 'Less', more: 'More', levels: [0, 1, 2, 3, 4] }}
            ></cv-heatmap>
        `;
    }

    /** Stacked bar chart of tokens over time, one segment per model. Bars are grouped by day, or
     *  by ISO week when there are many days, so it stays readable across ranges. Order of models
     *  matches the breakdown (so colors line up with the bars below); native tooltip per bar. */
    // Returns a keyed directive (not a bare TemplateResult) so the chart subtree is rebuilt on
    // scope/range change — see the keyed() call for why (fluent-tooltip anchor re-binding).
    private _renderModelChart(d: StatsResponse): unknown {
        const days = d.dailyModelTokens ?? [];
        if (days.length === 0) {
            return html``;
        }

        // Color index by model id, following the breakdown order.
        const colorOf = new Map<string, string>();
        d.modelBreakdown.forEach((m, i) => colorOf.set(m.model, modelColor(i)));

        // Group by day when few, else by ISO-ish week (yyyy-Www from the date's Monday).
        const weekly = days.length > 35;
        const bucketKey = (date: string): string => {
            if (!weekly) {
                return date;
            }
            const [y, m, dd] = date.split('-').map(Number);
            const dt = new Date(y, m - 1, dd);
            dt.setDate(dt.getDate() - ((dt.getDay() + 6) % 7)); // back to Monday
            return `${dt.getFullYear()}-${String(dt.getMonth() + 1).padStart(2, '0')}-${String(dt.getDate()).padStart(2, '0')}`;
        };

        // buckets: key → (model → tokens)
        const buckets = new Map<string, Map<string, number>>();
        for (const day of days) {
            const k = bucketKey(day.date);
            const byModel = buckets.get(k) ?? new Map<string, number>();
            const tm = (day.tokensByModel ?? {}) as Record<string, number>;
            for (const [model, tok] of Object.entries(tm)) {
                byModel.set(model, (byModel.get(model) ?? 0) + (tok || 0));
            }
            buckets.set(k, byModel);
        }

        // Activity (messages/sessions/tools) summed into the same buckets, for the hover card.
        const activity = new Map<string, { messages: number; sessions: number; tools: number }>();
        for (const a of d.dailyActivity) {
            const k = bucketKey(a.date);
            const acc = activity.get(k) ?? { messages: 0, sessions: 0, tools: 0 };
            acc.messages += a.messageCount;
            acc.sessions += a.sessionCount;
            acc.tools += a.toolCallCount;
            activity.set(k, acc);
        }

        const keys = [...buckets.keys()].sort();
        const totals = keys.map((k) =>
            [...(buckets.get(k)?.values() ?? [])].reduce((s, v) => s + v, 0),
        );
        const max = Math.max(1, ...totals);

        // Y axis: round the peak up to a "nice" ceiling (1/2/5 × 10ⁿ) so bars don't touch the top and
        // the ticks read cleanly; bars scale on this ceiling, not the raw max, so the ticks line up.
        const TICKS = 4; // → 5 gridlines counting 0
        const niceMax = niceCeil(max, TICKS);
        const ticks = Array.from({ length: TICKS + 1 }, (_, i) => (niceMax / TICKS) * i);

        // X axis: ~10 date labels evenly spaced (all of them would overlap on wide ranges).
        const xStep = Math.max(1, Math.ceil(keys.length / 10));

        // keyed on scope/range/bar-count: fluent-tooltip binds its anchor once in connectedCallback,
        // so on a plain re-render (scope/range switch) Lit reuses the tooltip nodes and the anchor
        // link goes stale → the tooltip jumps to 0,0 (top-left). A changing key forces Lit to discard
        // and rebuild the chart subtree, re-running each tooltip's connectedCallback to re-anchor.
        return keyed(
            `${this._scope}-${this._range}-${keys.length}`,
            html`
                <div class="chart">
                    <div class="chart-yaxis">
                        ${[...ticks].reverse().map((t) => html`<span class="chart-ytick">${formatAxis(t)}</span>`)}
                    </div>
                    <div class="chart-plot">
                        <div class="chart-grid">
                            ${ticks.map(() => html`<span class="chart-gridline"></span>`)}
                        </div>
                        <div class="chart-bars">
                            ${keys.map((k, ki) => {
                                const byModel = buckets.get(k) ?? new Map();
                                // The hover card, identical to the heatmap's: rows in breakdown/colour
                                // order, non-zero; plus this bucket's activity counts.
                                const rows: DayRow[] = d.modelBreakdown
                                    .map((m) => ({
                                        name: m.model,
                                        tok: byModel.get(m.model) ?? 0,
                                        color: colorOf.get(m.model),
                                    }))
                                    .filter((r) => r.tok > 0);
                                const act = activity.get(k) ?? {
                                    messages: 0,
                                    sessions: 0,
                                    tools: 0,
                                };
                                const info: DayInfo = { date: k, ...act, rows };
                                const barId = `bar-${ki}`;
                                return html`
                                    <div
                                        id=${barId}
                                        class="chart-bar"
                                        style="height:${(totals[ki] / niceMax) * 100}%"
                                    >
                                        ${d.modelBreakdown.map((m) => {
                                            const tok = byModel.get(m.model) ?? 0;
                                            if (tok === 0) {
                                                return nothing;
                                            }
                                            return html`<span
                                                class="chart-seg"
                                                style="flex:${tok};background:${colorOf.get(m.model)}"
                                            ></span>`;
                                        })}
                                    </div>
                                    <fluent-tooltip anchor=${barId}
                                        >${this._dayTip(info)}</fluent-tooltip
                                    >
                                `;
                            })}
                        </div>
                    </div>
                    <div class="chart-xaxis-pad"></div>
                    <div class="chart-xaxis">
                        ${keys.map((k, ki) => html`<span class="chart-xtick">${ki % xStep === 0 ? formatDay(k) : ''}</span>`)}
                    </div>
                </div>
            `,
        );
    }

    private _renderModels(d: StatsResponse | null): TemplateResult {
        // While loading (d null) keep the tab area occupied so switching combos doesn't jump.
        if (!d) {
            return html`<div class="empty">${'—'}</div>`;
        }
        if (d.modelBreakdown.length === 0) {
            return html`<div class="empty">No per-model data.</div>`;
        }
        return html`
            ${this._renderModelChart(d)}
            <div class="models">
                ${d.modelBreakdown.map(
                    (m, i) => html`
                        <div class="model">
                            <div class="model-head">
                                <span class="model-dot" style="background:${modelColor(i)}"></span>
                                <span class="model-name">${m.model}</span>
                                <span class="model-pct">${m.percentage.toFixed(1)}%</span>
                            </div>
                            <fluent-progress-bar
                                class="model-bar"
                                value=${Math.round(m.percentage)}
                            ></fluent-progress-bar>
                            <div class="model-tok">
                                ${formatTokens(m.inputTokens)} in · ${formatTokens(m.outputTokens)}
                                out
                            </div>
                        </div>
                    `,
                )}
            </div>
        `;
    }

    private _renderBody(): TemplateResult {
        if (this._error) {
            return html`<div class="status">Statistics unavailable.</div>`;
        }
        // Pass _data (may be null while loading) straight through — the tab renderers show
        // "—" placeholders in the fixed layout, so a combo change never collapses the dialog.
        return this._tab === 'overview'
            ? this._renderOverview(this._data)
            : this._renderModels(this._data);
    }

    override render() {
        // Create-on-open: nothing in the DOM while closed → the dialog is destroyed on close.
        if (!this.open) {
            return nothing;
        }
        return html`
            <fluent-dialog type="modal" aria-label="Statistics" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <h2 slot="title">Statistics</h2>
                    <fluent-button
                        slot="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Close"
                        >${unsafeHTML(Dismiss16Regular)}</fluent-button
                    >
                    ${this._renderCombos()}
                    <!-- One indeterminate bar for the whole view (not per value): shown while
                         loading OR while a background index pass is still running (partial data).
                         The fixed grid below keeps its size, so no resize. -->
                    <fluent-progress-bar
                        class="loading ${this._busy ? '' : 'idle'}"
                        aria-label="Loading"
                    ></fluent-progress-bar>
                    ${this._renderTabs()} ${this._renderBody()}
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-stats-dialog': CvStatsDialog;
    }
}

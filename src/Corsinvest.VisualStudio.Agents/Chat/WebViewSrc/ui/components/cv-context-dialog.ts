/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing, type TemplateResult } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import ChevronRight16Regular from '@fluentui/svg-icons/icons/chevron_right_16_regular.svg';
import { dialogStyles, iconStyles } from '../styles/shared';
import { fetchContextUsage } from '../../core/lazy';
import { formatTokens } from '../../core/ai-models';
import { CvDialogBase } from './cv-dialog-base';
import { displayPath } from '../../core/path';
import { state as appState } from '../../core/state';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import type { IdeFileNotification } from '../../core/types';
import type {
    GetContextUsageResponse,
    ContextCategoryDto,
    ContextGridCellDto,
    ContextTokenGroupDto,
} from '../../core/types';

// Color per category. The CLI reuses color-symbols (System tools + its deferred both
// "inactive"; System prompt + Free space both "promptBorder"), so we key on the stable
// category NAME to give each a distinct swatch — like VS Code. Vivid, readable hues on both
// themes; Free space is the neutral "empty" grey (never a colored fill).
const FREE_SPACE = 'var(--colorNeutralStroke1)';
// Saturated, theme-aware swatches (Fluent *BorderActive tokens are the vivid pure hues).
const CATEGORY_BY_NAME: Record<string, string> = {
    'System prompt': 'var(--colorNeutralForeground3)',
    'System tools': 'var(--colorPaletteBlueBorderActive)',
    'System tools (deferred)': 'var(--colorPaletteTealBorderActive)',
    'Custom agents': 'var(--colorPaletteGreenBorderActive)',
    'Memory files': 'var(--colorPaletteYellowBorderActive)',
    Skills: 'var(--colorPaletteDarkOrangeBorderActive)',
    Messages: 'var(--colorPaletteLavenderBorderActive)',
    'Free space': FREE_SPACE,
};
function categoryColor(color: string | undefined, name?: string): string {
    if (name && CATEGORY_BY_NAME[name]) {
        return CATEGORY_BY_NAME[name];
    }
    // Fallback for grid cells that only carry a color-symbol.
    return color === 'promptBorder' ? FREE_SPACE : 'var(--colorNeutralForeground3)';
}

/**
 * Context-usage dialog: a snapshot of how the CURRENT session fills its context
 * window (categories, memory-map, per-message breakdown, tree of memory/agents/
 * skills/mcp/commands). Fetches fresh on each open — the window changes per turn.
 * Distinct from cv-usage-dialog (plan/account). Uses fluent-dialog (backdrop +
 * focus-trap + Esc for free); `open` drives show()/hide().
 */
@customElement('cv-context-dialog')
export class CvContextDialog extends CvDialogBase {
    static override styles = [
        dialogStyles,
        iconStyles,
        css`
            .header {
                margin-bottom: 10px;
            }
            .model {
                font-size: 1.05em;
                font-weight: var(--fontWeightSemibold);
            }
            .tokens {
                color: var(--colorNeutralForeground2);
                font-variant-numeric: tabular-nums;
            }
            /* Segmented bar: colored slices sized by token share. */
            .bar {
                display: flex;
                height: 8px;
                border-radius: var(--borderRadiusSmall);
                overflow: hidden;
                /* The unfilled tail is Free space — a visible neutral track, not a black gap. */
                background: var(--colorNeutralStroke2);
                margin-bottom: 10px;
            }
            .bar-seg {
                height: 100%;
            }
            /* Memory-map: 10x20 grid of SQUARE cells (aspect-ratio keeps them square). */
            .grid {
                display: grid;
                grid-template-columns: repeat(20, 1fr);
                gap: 2px;
                margin-bottom: 14px;
            }
            .grid-row {
                display: contents;
            }
            .grid-cell {
                aspect-ratio: 1;
                border-radius: 1px;
                min-width: 0;
            }
            /* Column header above the category list (Category / Tokens / Usage). */
            .colhead {
                display: flex;
                align-items: center;
                gap: 8px;
                padding: 0 2px 4px;
                color: var(--colorNeutralForeground3);
                font-size: 0.82em;
                text-transform: uppercase;
                letter-spacing: 0.04em;
                border-bottom: 1px solid var(--colorNeutralStroke2);
            }
            /* Grey slash-command hint next to a tree title (e.g. /memory, /agents). */
            .slash {
                color: var(--colorNeutralForeground3);
                font-weight: var(--fontWeightRegular);
                margin-left: 4px;
            }
            /* Category list + tree rows. */
            .list,
            .trees {
                display: flex;
                flex-direction: column;
                overflow-x: hidden; /* long paths wrap; never scroll the dialog sideways */
            }
            /* The list sits right under .colhead (already bordered) — only the trees
             * block needs its own separator from the list above it. */
            .trees {
                margin-top: 8px;
                border-top: 1px solid var(--colorNeutralStroke2);
                padding-top: 6px;
            }
            .row {
                display: flex;
                align-items: center;
                gap: 8px;
                padding: 4px 2px;
                font-variant-numeric: tabular-nums;
            }
            .row-toggle {
                cursor: pointer;
                border-radius: var(--borderRadiusSmall);
            }
            .row-toggle:hover {
                background: var(--colorSubtleBackgroundHover, rgba(127, 127, 127, 0.1));
            }
            .dot {
                width: 10px;
                height: 10px;
                border-radius: 2px;
                flex: 0 0 auto;
            }
            .name {
                flex: 1 1 auto;
                min-width: 0;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .count {
                color: var(--colorNeutralForeground3);
            }
            .tok {
                color: var(--colorNeutralForeground1);
                flex: 0 0 auto;
            }
            .pct {
                color: var(--colorNeutralForeground3);
                flex: 0 0 auto;
                min-width: 3.2em;
                text-align: right;
            }
            /* Chevron rotates when the section is open. */
            .chevron {
                display: inline-flex;
                flex: 0 0 auto;
                color: var(--colorNeutralForeground3);
                transition: transform 0.12s ease;
            }
            .chevron svg {
                width: 14px;
                height: 14px;
            }
            /* Keeps the dot aligned with expandable rows' dots (same width as the chevron). */
            .chevron-spacer {
                width: 14px;
                flex: 0 0 auto;
            }
            .row.is-open .chevron {
                transform: rotate(90deg);
            }
            /* Expanded detail (message breakdown / tree children): indented sub-rows. A long
             * list scrolls inside itself instead of stretching the dialog. */
            .detail {
                padding: 2px 0 6px 18px;
                max-height: 220px;
                overflow-y: auto;
                overflow-x: hidden; /* long names wrap instead of scrolling sideways */
            }
            .subrow {
                display: flex;
                align-items: flex-start; /* multi-line names keep the value top-aligned */
                gap: 8px;
                padding: 2px 2px;
                color: var(--colorNeutralForeground2);
                font-variant-numeric: tabular-nums;
            }
            .sub-name {
                flex: 1 1 auto;
                min-width: 0;
                /* Wrap long names (paths, skill ids) mid-token instead of scrolling sideways. */
                overflow-wrap: anywhere;
            }
            /* Path button rendered inline like a link (was a fluent-link href="#" — that triggered the
             * global navigation handler and opened the file twice). Layout-only; keeps fluent pure. */
            .path-link {
                display: inline;
                min-width: 0;
                vertical-align: baseline;
            }
            /* Nested collapsible group inside an expanded tree (source/type/server groups). */
            .subtree-head {
                display: flex;
                align-items: center;
                gap: 8px;
                padding: 3px 2px;
                color: var(--colorNeutralForeground2);
                font-variant-numeric: tabular-nums;
            }
            .subtree-head .chevron svg {
                width: 14px;
                height: 14px;
            }
            .subtree-head.is-open .chevron {
                transform: rotate(90deg);
            }
            /* Second indentation level for the group's leaf rows. */
            .subtree-body {
                padding-left: 16px;
            }
            .footer {
                margin-top: 12px;
                padding-top: 8px;
                border-top: 1px solid var(--colorNeutralStroke2);
                color: var(--colorNeutralForeground3);
                font-size: 0.92em;
            }
        `,
    ];

    @state() private _data: GetContextUsageResponse | null = null;
    @state() private _loading = false;
    @state() private _error = false;
    // Which collapsible sections are open (messages breakdown + the trees). Default: all closed.
    @state() private _expanded = new Set<string>();

    override willUpdate(changed: Map<string, unknown>): void {
        if (changed.has('open') && this.open) {
            // Fresh fetch each open — context changes every turn, no stale caching.
            this._data = null;
            this._error = false;
            this._loading = true;
            fetchContextUsage()
                .then((d) => {
                    this._data = d;
                    this._loading = false;
                })
                .catch(() => {
                    this._error = true;
                    this._loading = false;
                });
        }
    }

    private _toggle(key: string): void {
        const next = new Set(this._expanded);
        if (next.has(key)) {
            next.delete(key);
        } else {
            next.add(key);
        }
        this._expanded = next;
    }

    private _pct(tokens: number, max: number): string {
        if (max <= 0) {
            return '0%';
        }
        const p = (tokens / max) * 100;
        return p >= 0.1 ? `${p.toFixed(1)}%` : '<0.1%';
    }

    /** Segmented bar: one colored slice per category, width = tokens/maxTokens. */
    private _renderBar(cats: ContextCategoryDto[], max: number): TemplateResult {
        return html`
            <div class="bar">
                ${cats.map((c) => {
                    const w = max > 0 ? (c.tokens / max) * 100 : 0;
                    return c.name === 'Free space' || w <= 0
                        ? nothing
                        : html`<span
                              class="bar-seg"
                              style="width:${w}%;background:${categoryColor(c.color, c.name)}"
                              title="${c.name}: ${formatTokens(c.tokens)}"
                          ></span>`;
                })}
            </div>
        `;
    }

    /** 10x20 memory-map: one square per grid cell, colored by its category. */
    private _renderGrid(rows: ContextGridCellDto[][]): TemplateResult {
        return html`
            <div class="grid">
                ${rows.map(
                    (row) =>
                        html`<div class="grid-row">
                            ${row.map(
                                (cell) =>
                                    html`<span
                                        class="grid-cell"
                                        style="background:${
                                            cell.isFilled
                                                ? categoryColor(cell.color, cell.categoryName)
                                                : 'var(--colorNeutralBackground4)'
                                        }"
                                        title="${cell.categoryName}: ${formatTokens(cell.tokens)}"
                                    ></span>`,
                            )}
                        </div>`,
                )}
            </div>
        `;
    }

    /** One category row (colored dot + name + tokens + %). Expandable ones get a chevron. */
    private _catRow(c: ContextCategoryDto, max: number, expandKey?: string): TemplateResult {
        const open = expandKey ? this._expanded.has(expandKey) : false;
        return html`
            <div
                class="row ${expandKey ? 'row-toggle' : ''} ${open ? 'is-open' : ''}"
                @click=${expandKey ? () => this._toggle(expandKey) : undefined}
            >
                ${
                    expandKey
                        ? html`<span class="chevron">${unsafeHTML(ChevronRight16Regular)}</span>`
                        : html`<span class="chevron-spacer"></span>`
                }
                <span class="dot" style="background:${categoryColor(c.color, c.name)}"></span>
                <span class="name">${c.name}</span>
                <span class="tok">${formatTokens(c.tokens)}</span>
                <span class="pct">${this._pct(c.tokens, max)}</span>
            </div>
        `;
    }

    /** Open a file by absolute path in the VS editor (reuses the ideFile channel). */
    private _openFile(path: string): void {
        bridge.sendNotification<IdeFileNotification>(Msg.fromWebView.open.ideFile, {
            filePath: path,
            startLine: 0,
            endLine: 0,
        });
    }

    /** A sub-row whose name is a clickable file link (opens in the editor). `path` is the absolute
     *  file path; `suffix` is the trailing info shown after the name (e.g. "· Project"). */
    private _subRowFile(path: string, suffix: string, tokens: number): TemplateResult {
        const shown = displayPath(path, appState.workingDirectory, appState.ui.showRelativePaths);
        return html`
            <div class="subrow">
                <span class="sub-name">
                    <fluent-button
                        class="path-link"
                        appearance="transparent"
                        size="small"
                        title=${path}
                        @click=${(): void => this._openFile(path)}
                        >${shown}</fluent-button
                    >${suffix}
                </span>
                <span class="tok">${formatTokens(tokens)}</span>
            </div>
        `;
    }

    /** A sub-row inside an expanded section (indented label + tokens). */
    private _subRow(label: string, tokens: number): TemplateResult {
        return html`
            <div class="subrow">
                <span class="sub-name">${label}</span>
                <span class="tok">${formatTokens(tokens)}</span>
            </div>
        `;
    }

    private _renderMessagesBreakdown(): TemplateResult | typeof nothing {
        const mb = this._data?.messageBreakdown;
        if (!mb || !this._expanded.has('messages')) {
            return nothing;
        }
        const parts: Array<[string, number]> = [
            ['Tool calls', mb.toolCallTokens],
            ['Tool results', mb.toolResultTokens],
            ['Attachments', mb.attachmentTokens],
            ['Assistant', mb.assistantMessageTokens],
            ['User', mb.userMessageTokens],
            ['Redirected', mb.redirectedContextTokens],
            ['Unattributed', mb.unattributedTokens],
        ];
        const group = (title: string, items: ContextTokenGroupDto[] | null | undefined) =>
            !items || items.length === 0
                ? nothing
                : this._subTree(
                      'messages',
                      title,
                      items.length,
                      items.reduce((s, g) => s + g.tokens, 0),
                      () =>
                          html`${[...items]
                              .sort((a, b) => b.tokens - a.tokens || a.name.localeCompare(b.name))
                              .map((g) => this._subRow(g.name, g.tokens))}`,
                  );
        return html`
            <div class="detail">
                ${parts.filter(([, t]) => t > 0).map(([l, t]) => this._subRow(l, t))}
                ${group('By tool type', mb.toolCallsByType)}
                ${group('By attachment type', mb.attachmentsByType)}
            </div>
        `;
    }

    /** A collapsible tree section (Memory / Agents / Skills / MCP / Commands). `slashHint`
     *  is the CLI command that manages it (e.g. /memory), shown grey like VS Code. */
    private _tree(
        key: string,
        title: string,
        count: number,
        tokens: number,
        body: () => TemplateResult,
        slashHint?: string,
    ): TemplateResult {
        const open = this._expanded.has(key);
        return html`
            <div class="row row-toggle ${open ? 'is-open' : ''}" @click=${() => this._toggle(key)}>
                <span class="chevron">${unsafeHTML(ChevronRight16Regular)}</span>
                <span class="name">
                    ${title} <span class="count">(${count})</span>
                    ${slashHint ? html`<span class="slash">${slashHint}</span>` : nothing}
                </span>
                <span class="tok">${formatTokens(tokens)}</span>
            </div>
            ${open ? html`<div class="detail">${body()}</div>` : nothing}
        `;
    }

    /** A nested collapsible sub-group inside an expanded tree (chevron + name + count + tokens).
     *  `parentKey` namespaces the expand-state so identical group names in different trees don't
     *  collide; NUL joins them because an MCP serverName can contain ':'. */
    private _subTree(
        parentKey: string,
        name: string,
        count: number,
        tokens: number,
        body: () => TemplateResult,
    ): TemplateResult {
        const key = `${parentKey}\0${name}`;
        const open = this._expanded.has(key);
        return html`
            <div
                class="subtree-head row-toggle ${open ? 'is-open' : ''}"
                @click=${() => this._toggle(key)}
            >
                <span class="chevron">${unsafeHTML(ChevronRight16Regular)}</span>
                <span class="sub-name">${name} <span class="count">(${count})</span></span>
                <span class="tok">${formatTokens(tokens)}</span>
            </div>
            ${open ? html`<div class="subtree-body">${body()}</div>` : nothing}
        `;
    }

    /** Body of an expandable tree, grouped by keyOf. With ≥2 distinct keys it renders a collapsible
     *  sub-group per key (sorted by tokens desc, then name); with a single key it degrades to a flat
     *  list of rows (no pointless chevron over one child). Leaf rows/groups are always token-sorted. */
    private _groupedBody<T>(
        parentKey: string,
        items: T[],
        keyOf: (it: T) => string,
        tokensOf: (it: T) => number,
        row: (it: T) => TemplateResult,
    ): TemplateResult {
        const byKey = new Map<string, T[]>();
        for (const it of items) {
            const k = keyOf(it);
            (byKey.get(k) ?? byKey.set(k, []).get(k)!).push(it);
        }
        const sortItems = (list: T[]) =>
            [...list].sort((a, b) => tokensOf(b) - tokensOf(a) || keyOf(a).localeCompare(keyOf(b)));
        if (byKey.size <= 1) {
            return html`${sortItems(items).map(row)}`;
        }
        const groups = [...byKey.entries()].map(([name, list]) => ({
            name,
            list: sortItems(list),
            tokens: list.reduce((s, it) => s + tokensOf(it), 0),
        }));
        groups.sort((a, b) => b.tokens - a.tokens || a.name.localeCompare(b.name));
        return html`${groups.map((g) =>
            this._subTree(
                parentKey,
                g.name,
                g.list.length,
                g.tokens,
                () => html`${g.list.map(row)}`,
            ),
        )}`;
    }

    private _renderTrees(d: GetContextUsageResponse): TemplateResult {
        const mcpTokens = (d.mcpTools ?? []).reduce((s, t) => s + t.tokens, 0);
        return html`
            ${
                (d.memoryFiles?.length ?? 0) > 0
                    ? this._tree(
                          'memory',
                          'Memory files',
                          d.memoryFiles.length,
                          d.memoryFiles.reduce((s, f) => s + f.tokens, 0),
                          () =>
                              this._groupedBody(
                                  'memory',
                                  d.memoryFiles,
                                  (f) => f.type,
                                  (f) => f.tokens,
                                  (f) => this._subRowFile(f.path, '', f.tokens),
                              ),
                          '/memory',
                      )
                    : nothing
            }
            ${
                (d.agents?.length ?? 0) > 0
                    ? this._tree(
                          'agents',
                          'Custom agents',
                          d.agents.length,
                          d.agents.reduce((s, a) => s + a.tokens, 0),
                          () =>
                              this._groupedBody(
                                  'agents',
                                  d.agents,
                                  (a) => a.source,
                                  (a) => a.tokens,
                                  (a) => this._subRow(a.agentType, a.tokens),
                              ),
                          '/agents',
                      )
                    : nothing
            }
            ${
                d.skills && d.skills.totalSkills > 0
                    ? this._tree('skills', 'Skills', d.skills.totalSkills, d.skills.tokens, () =>
                          this._groupedBody(
                              'skills',
                              d.skills.skillFrontmatter ?? [],
                              (s) => s.source,
                              (s) => s.tokens,
                              (s) => this._subRow(s.name, s.tokens),
                          ),
                      )
                    : nothing
            }
            ${
                (d.mcpTools?.length ?? 0) > 0
                    ? this._tree('mcp', 'MCP tools', d.mcpTools.length, mcpTokens, () =>
                          this._groupedBody(
                              'mcp',
                              d.mcpTools,
                              (t) => t.serverName,
                              (t) => t.tokens,
                              (t) => this._subRow(t.name, t.tokens),
                          ),
                      )
                    : nothing
            }
            ${
                d.slashCommands && d.slashCommands.totalCommands > 0
                    ? html`<div class="row">
                          <span class="name"
                              >Slash commands
                              <span class="count">(${d.slashCommands.totalCommands})</span></span
                          >
                          <span class="tok">${formatTokens(d.slashCommands.tokens)}</span>
                      </div>`
                    : nothing
            }
        `;
    }

    private _renderBody() {
        if (this._loading) {
            return html`<div class="cv-dialog-loading">Loading…</div>`;
        }
        if (this._error || !this._data) {
            return html`<div class="cv-dialog-loading">Context usage unavailable</div>`;
        }
        const d = this._data;
        const pct = Math.max(0, Math.min(100, Math.round(d.percentage)));
        const max = d.maxTokens;
        // Categories sorted by tokens desc, Free space always last. Tie-break by name so the
        // order is deterministic (equal token counts otherwise arrive in an arbitrary order).
        const cats = [...(d.categories ?? [])].sort((a, b) => {
            if (a.name === 'Free space') {
                return 1;
            }
            if (b.name === 'Free space') {
                return -1;
            }
            return b.tokens - a.tokens || a.name.localeCompare(b.name);
        });
        return html`
            <div class="header">
                <div class="model">${d.model}</div>
                <div class="tokens">
                    ${formatTokens(d.totalTokens)} / ${formatTokens(max)} (${pct}%)
                </div>
            </div>
            ${this._renderBar(d.categories ?? [], max)}
            ${(d.gridRows?.length ?? 0) > 0 ? this._renderGrid(d.gridRows) : nothing}
            <div class="colhead">
                <span class="name">Category</span>
                <span class="tok">Tokens</span>
                <span class="pct">Usage</span>
            </div>
            <div class="list">
                ${cats.map((c) => {
                    const isMessages = c.name === 'Messages';
                    return html`
                        ${this._catRow(c, max, isMessages ? 'messages' : undefined)}
                        ${isMessages ? this._renderMessagesBreakdown() : nothing}
                    `;
                })}
            </div>
            <div class="trees">${this._renderTrees(d)}</div>
            <div class="footer">
                Auto-compact: ${d.isAutoCompactEnabled ? 'on' : 'off'}
                ${d.isAutoCompactEnabled ? html`· threshold ${formatTokens(d.autoCompactThreshold)}` : nothing}
                ${d.autocompactSource ? html`· ${d.autocompactSource}` : nothing}
            </div>
        `;
    }

    override render() {
        // Create-on-open: nothing in the DOM while closed → the dialog is destroyed on close.
        if (!this.open) {
            return nothing;
        }
        return html`
            <fluent-dialog type="modal" aria-label="Context usage" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <h2 slot="title">Context usage</h2>
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
        'cv-context-dialog': CvContextDialog;
    }
}

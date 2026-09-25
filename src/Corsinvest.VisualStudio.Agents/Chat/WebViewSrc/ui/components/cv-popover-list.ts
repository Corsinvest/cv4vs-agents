/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing, type TemplateResult } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { iconStyles } from '../styles/shared';

/** A section header + its items, when a list is grouped (e.g. the command palette). */
export interface ListSection<T> {
    label: string;
    items: T[];
    /** Optional right-aligned note on the header, e.g. a keyboard shortcut. */
    hint?: string;
}

/**
 * Generic navigable list popover (the engine behind cv-at-menu / cv-command-menu / cv-model-list,
 * and future lists like history/pin/media). It OWNS the behaviour — anchored popover, ↑/↓
 * wrap-around navigation, scroll-into-view, active state, optional search box — and DELEGATES the
 * row content to the caller via the `renderRow` render-prop. Callers stay thin: data + renderRow
 * + their own select event.
 *
 * Navigation runs over the navigable items only (`items` filtered by `isNavigable`), so an index
 * skips both section headings and non-navigable rows (e.g. disabled models). A searchable list
 * either filters itself (the caller supplies `searchText`) or emits `search-input` and the caller
 * re-supplies items (Fuse, in the command palette).
 */
@customElement('cv-popover-list')
export class CvPopoverList extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* Anchored above the composer. :host is placed in the parent's positioned toolbar. */
            :host {
                position: absolute;
                bottom: calc(100% + 4px);
                left: 8px;
                right: 8px;
                z-index: 1000;
            }
            .popover {
                background: var(--colorNeutralBackground1);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                box-shadow: var(--shadow8);
                max-height: 340px;
                overflow: hidden;
                display: flex;
                flex-direction: column;
                font-size: var(--fontSizeBase300);
            }
            /* Search box (fluent-text-input): full width, overriding Fluent's max-width. */
            .search {
                margin: 6px 6px 2px;
                width: calc(100% - 12px);
                max-width: none;
            }
            .list {
                overflow-y: auto;
                max-height: 340px;
                padding: 4px;
            }
            .empty {
                padding: 10px 12px;
                color: var(--colorNeutralForeground3);
                font-family: var(--fontFamilyBase);
                font-size: var(--fontSizeBase200);
            }
            /* The optional header band, in a section heading's key. Centred rather than
               baseline-aligned: unlike a section it may hold a button, which a baseline leaves
               sitting low against the text. */
            .header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 8px;
                padding: 6px 4px 4px 8px;
                color: var(--colorNeutralForeground3);
                font-family: var(--fontFamilyBase);
                font-size: var(--fontSizeBase200);
                font-weight: var(--fontWeightSemibold);
            }
            /* The optional band below the list: outside the scroller like the header, so it stays
               in view — for a control that acts on the whole list rather than on one row. */
            .footer {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 8px;
                padding: 6px 12px;
                border-top: 1px solid var(--colorNeutralStroke2);
                color: var(--colorNeutralForeground2);
                font-family: var(--fontFamilyBase);
            }
            .section {
                display: flex;
                align-items: baseline;
                justify-content: space-between;
                gap: 8px;
                padding: 8px 8px 4px;
                color: var(--colorNeutralForeground3);
                font-family: var(--fontFamilyBase);
                font-size: var(--fontSizeBase200);
                font-weight: var(--fontWeightSemibold);
            }
            .section-hint {
                font-weight: var(--fontWeightRegular);
                opacity: 0.8;
            }
            .section:not(:first-child) {
                margin-top: 4px;
                border-top: 1px solid var(--colorNeutralStroke2);
                padding-top: 8px;
            }
            /* Row shell (content comes from the caller's renderRow). */
            .row {
                display: flex;
                align-items: center;
                gap: var(--spacingHorizontalS);
                padding: 4px 8px;
                border-radius: var(--borderRadiusMedium);
                cursor: pointer;
                color: var(--colorNeutralForeground2);
                font-family: var(--fontFamilyBase);
                font-size: 1em;
                line-height: 1.2;
                min-width: 0;
            }
            .row:hover {
                background: var(--colorNeutralBackground1Hover);
                color: var(--colorNeutralForeground2Hover);
            }
            .row.selected {
                background: var(--colorBrandBackground);
                color: var(--colorNeutralForegroundOnBrand);
            }
            /* subtleActive: the cursor without the brand fill — see the property. */
            :host([subtleactive]) .row.selected {
                background: var(--colorNeutralBackground1Hover);
                color: var(--colorNeutralForeground2Hover);
            }
            /* isGrouped: rows handled as one. A rule down the left rather than merging them —
               they stay separate rows, each keeping its own actions. */
            .row.grouped {
                border-left: 2px solid var(--colorBrandStroke1);
                border-top-left-radius: 0;
                border-bottom-left-radius: 0;
                padding-left: 6px;
            }
            .row.disabled {
                cursor: default;
                opacity: 0.55;
            }
            .row.disabled:hover {
                background: transparent;
                color: var(--colorNeutralForeground2);
            }

            /* Row-content classes shared by the callers' renderRow markup (they render into
             * this component's shadow, so their styles must live here). */
            .row-icon {
                flex-shrink: 0;
                width: 20px;
                height: 20px;
                display: inline-flex;
                align-items: center;
                justify-content: center;
                color: var(--colorNeutralForeground3);
            }
            .row.selected .row-icon {
                color: var(--colorNeutralForegroundOnBrand);
            }
            .row-icon svg {
                width: 16px;
                height: 16px;
            }
            .row-label {
                flex: 1;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .row-trailing {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
                gap: 4px;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                text-align: right;
            }
            /* Free text beside the label (a command's argument hint): it gives way to the label,
               which must stay readable, instead of pushing it out of the row. */
            .row-hint {
                flex: 0 1 auto;
                min-width: 0;
                max-width: 50%;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
            }
            /* Glyph of the current value (e.g. the active permission mode), so the row,
             * the toolbar trigger and the picker all show the same icon. */
            .trailing-icon {
                display: inline-flex;
            }
            .trailing-icon svg {
                width: 14px;
                height: 14px;
                fill: currentColor;
            }
            /* Two-line cell (model rows): name + description stacked. */
            .row-text {
                flex: 1;
                min-width: 0;
                display: flex;
                flex-direction: column;
            }
            .row-desc {
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .row.selected .row-desc {
                color: var(--colorNeutralForegroundOnBrand);
            }
            .row-check {
                flex-shrink: 0;
                display: flex;
                align-items: center;
                /* Follow the text colour (theme-aware), not the brand accent. */
                color: var(--colorNeutralForeground1);
            }
            .row.selected .row-check {
                color: var(--colorNeutralForegroundOnBrand);
            }
            /* File row (cv-at-menu). */
            .item-icon {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
            }
            .item-name {
                flex: 1;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .item-dir {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                flex-shrink: 0;
                max-width: 45%;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                text-align: right;
            }
            .row.selected .item-dir {
                color: var(--colorNeutralForegroundOnBrand);
                opacity: 0.85;
            }
            /* Inline toggle switch / effort slider (command trailing controls). */
            .toggle {
                flex-shrink: 0;
            }
            .dots-wrap {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
                gap: 8px;
            }
            /* Wide enough for the longest level ("Extra high"): the value sits after the slider, so
               a box that grows with the word drags the slider sideways while you are dragging it. */
            .dots-val {
                min-width: 6em;
                text-align: right;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                text-transform: capitalize;
            }
            .row.selected cv-segmented-slider {
                --cv-slider-border: var(--colorNeutralForegroundOnBrand);
            }
            .row.selected .dots-val,
            .row.selected .row-trailing,
            .row.selected .row-hint {
                color: var(--colorNeutralForegroundOnBrand);
                opacity: 0.85;
            }
        `,
    ];

    /** Optional band above the list, supplied by the caller. A TemplateResult rather than a string
     *  because what goes there is not always only a title — the queue puts its clear button beside
     *  the count. Like renderRow: the caller says what, this owns where. */
    @property({ attribute: false }) header?: TemplateResult;
    /** Optional band below the list, the header's twin: the caller says what, this owns where. */
    @property({ attribute: false }) footer?: TemplateResult;
    /** Mark the cursor row with the hover tint instead of the brand fill. For a list you act ON
     *  rather than pick FROM: the fill announces "this is what Enter takes", which is wrong for a
     *  row that carries its own buttons — and a solid blue behind them leaves a red one no longer
     *  reading as a warning. */
    @property({ type: Boolean }) subtleActive = false;
    /** Which items belong together, when some of them do. Rows answering true get a rule down
     *  their left, saying they are handled as one — the queue's Alt+Enter groups leave as a single
     *  message. The caller knows what "together" means; this only draws it. */
    @property({ attribute: false }) isGrouped?: (item: unknown) => boolean;
    /** All items to SHOW (including non-navigable ones, e.g. disabled models). */
    @property({ attribute: false }) items: unknown[] = [];
    /** Render-prop for a row's content (the shell — selected state, click — is ours). */
    @property({ attribute: false }) renderRow!: (
        item: unknown,
        selected: boolean,
    ) => TemplateResult;
    /** Which items are navigable (default: all). Non-navigable rows render but are skipped by ↑/↓. */
    @property({ attribute: false }) isNavigable?: (item: unknown) => boolean;
    /** When set, rows are grouped under headings; navigation still runs over the flat navigable set. */
    @property({ attribute: false }) sections?: ListSection<unknown>[];
    @property() emptyText = 'No results';
    /** Ask for the search box. Shown only when there is something to choose between (more than
     *  one item), and kept for the rest of the open once shown — a filter narrowing the caller's
     *  items down to one must not take the box away while the user is typing in it. */
    @property({ type: Boolean }) searchable = false;
    @property() searchPlaceholder = 'Search…';
    /** The text an item is searched by. When set the list filters itself against its own query;
     *  when absent it only emits `search-input` and the caller re-supplies the items. */
    @property({ attribute: false }) searchText?: (item: unknown) => string;
    @property() query = '';

    @state() private _activeIdx = 0;
    @state() private _searchShown = false;

    @query('.list') private _list?: HTMLDivElement;
    @query('.search') private _search?: HTMLElement & { value: string };

    /** The flat list of navigable items, in display order — what ↑/↓ and pickActive index into. */
    private get _nav(): unknown[] {
        const nav = this.isNavigable;
        const items = this._filter(this.items);
        return nav ? items.filter(nav) : items;
    }

    /** The items left by the self-managed filter (`searchText`); all of them otherwise. */
    private _filter<T>(items: T[]): T[] {
        const text = this.searchText;
        const q = this.query.trim().toLowerCase();
        return text && q ? items.filter((it) => text(it).toLowerCase().includes(q)) : items;
    }

    override willUpdate(changed: Map<string, unknown>): void {
        if (!this.searchable) {
            this._searchShown = false;
        } else if (this.items.length > 1) {
            this._searchShown = true;
        }
        // Reset the cursor to the first navigable row when the visible SET changes
        // (filter typed, results replaced). But items/sections are rebuilt as fresh
        // array refs on every parent re-render — e.g. toggling a trailing switch/slider
        // calls requestUpdate — so a plain changed.has('items') fires on cosmetic
        // re-renders too and snaps the highlight back to the top. Reset only when the
        // navigable set actually differs (by count + identity signature), not on ref churn.
        if (changed.has('query') || changed.has('items') || changed.has('sections')) {
            const sig = this._navSignature();
            if (sig !== this._lastNavSig) {
                this._lastNavSig = sig;
                this._activeIdx = 0;
            }
        }
    }

    /** Cheap identity signature of the navigable set: count + each item's label/name/path.
     *  Changes when the filter results change; stable across cosmetic re-renders. */
    private _navSignature(): string {
        return this._nav
            .map((it) => {
                const o = it as { label?: string; name?: string; path?: string; id?: string };
                return o.id ?? o.path ?? o.label ?? o.name ?? '';
            })
            .join('\0');
    }
    private _lastNavSig = '';

    override updated(changed: Map<string, unknown>): void {
        if (changed.has('_searchShown') && this._searchShown) {
            requestAnimationFrame(() => this._search?.focus());
        }
    }

    /** Move the cursor by delta (±1), wrapping at the ends (last↔first). */
    moveSelection(delta: number): void {
        const len = this._nav.length;
        if (len === 0) {
            return;
        }
        this._activeIdx = (this._activeIdx + delta + len) % len;
        queueMicrotask(() => this._scrollActiveIntoView());
    }

    /** Move the cursor by one visible page (dir ±1). Clamped at the ends rather than wrapped like
     *  the arrows: a jump of a whole page that lands at the other end loses you where you were. */
    movePage(dir: number): void {
        const len = this._nav.length;
        const row = this._list?.querySelector<HTMLElement>('.row.navigable');
        if (len === 0 || !this._list || !row) {
            return;
        }
        const page = Math.max(1, Math.floor(this._list.clientHeight / row.offsetHeight) - 1);
        this._activeIdx = Math.max(0, Math.min(len - 1, this._activeIdx + dir * page));
        queueMicrotask(() => this._scrollActiveIntoView());
    }

    /** Place the cursor on a specific navigable index (e.g. the active model on open). */
    setActive(navIndex: number): void {
        const len = this._nav.length;
        if (len === 0) {
            return;
        }
        this._activeIdx = Math.max(0, Math.min(len - 1, navIndex));
        queueMicrotask(() => this._scrollActiveIntoView());
    }

    /** Confirm the selected row: emit `select` with the navigable item. */
    pickActive(): void {
        const item = this._nav[this._activeIdx];
        if (item !== undefined) {
            this._emitSelect(item);
        }
    }

    private _emitSelect(item: unknown): void {
        this.dispatchEvent(
            new CustomEvent('select', { detail: { item }, bubbles: true, composed: true }),
        );
    }

    private _scrollActiveIntoView(): void {
        const list = this._list;
        const el = list?.querySelectorAll<HTMLElement>('.row.navigable')[this._activeIdx];
        if (!el || !list) {
            return;
        }
        const top = el.offsetTop;
        const bottom = top + el.offsetHeight;
        if (top < list.scrollTop) {
            list.scrollTop = top;
        } else if (bottom > list.scrollTop + list.clientHeight) {
            list.scrollTop = bottom - list.clientHeight;
        }
    }

    private _onSearchInput = (e: Event): void => {
        const q = (e.currentTarget as HTMLElement & { value?: string }).value ?? '';
        if (this.searchText) {
            this.query = q;
        }
        this.dispatchEvent(
            new CustomEvent('search-input', {
                detail: { query: q },
                bubbles: true,
                composed: true,
            }),
        );
    };

    private _onSearchKeyDown = (e: KeyboardEvent): void => {
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            this.moveSelection(1);
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            this.moveSelection(-1);
        } else if (e.key === 'PageDown' || e.key === 'PageUp') {
            e.preventDefault();
            this.movePage(e.key === 'PageDown' ? 1 : -1);
        } else if (e.key === 'Enter') {
            e.preventDefault();
            this.pickActive();
        } else if (e.key === 'Escape') {
            e.preventDefault();
            this.dispatchEvent(new CustomEvent('close-list', { bubbles: true, composed: true }));
        }
    };

    /** One row: the shell (state + events) wrapping the caller's content. `navIndex` is its
     *  position in the navigable set (−1 for non-navigable rows, which never get selected). */
    private _row(item: unknown, navIndex: number): TemplateResult {
        const navigable = navIndex >= 0;
        const selected = navigable && navIndex === this._activeIdx;
        const cls = [
            'row',
            navigable ? 'navigable' : 'disabled',
            selected ? 'selected' : '',
            this.isGrouped?.(item) ? 'grouped' : '',
        ].join(' ');
        return html`
            <div
                class=${cls}
                @mousedown=${(e: Event) => e.preventDefault()}
                @mouseenter=${() => {
                    if (navigable) {
                        this._activeIdx = navIndex;
                    }
                }}
                @click=${() => {
                    if (navigable) {
                        this._emitSelect(item);
                    }
                }}
            >
                ${this.renderRow(item, selected)}
            </div>
        `;
    }

    private _renderRows(): TemplateResult {
        const isNav = this.isNavigable ?? (() => true);
        let n = -1; // running navigable index (skips headings + non-navigable rows)
        const rowOf = (item: unknown): TemplateResult => {
            const navigable = isNav(item);
            if (navigable) {
                n += 1;
            }
            return this._row(item, navigable ? n : -1);
        };
        if (this.sections) {
            return html`${this.sections.map(
                (s) => html`
                    <div class="section">
                        <span>${s.label}</span>
                        ${s.hint ? html`<span class="section-hint">${s.hint}</span>` : nothing}
                    </div>
                    ${this._filter(s.items).map(rowOf)}
                `,
            )}`;
        }
        return html`${this._filter(this.items).map(rowOf)}`;
    }

    override render() {
        const empty = this.sections
            ? this.sections.every((s) => this._filter(s.items).length === 0)
            : this._filter(this.items).length === 0;
        return html`
            <div class="popover">
                ${this.header ? html`<div class="header">${this.header}</div>` : nothing}
                ${
                    this._searchShown
                        ? html`<fluent-text-input
                              class="search"
                              type="text"
                              placeholder=${this.searchPlaceholder}
                              aria-label="Filter"
                              .value=${this.query}
                              @input=${this._onSearchInput}
                              @keydown=${this._onSearchKeyDown}
                              @mousedown=${(e: Event) => e.stopPropagation()}
                          ></fluent-text-input>`
                        : nothing
                }
                <div class="list">
                    ${empty ? html`<div class="empty">${this.emptyText}</div>` : this._renderRows()}
                </div>
                ${this.footer ? html`<div class="footer">${this.footer}</div>` : nothing}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-popover-list': CvPopoverList;
    }
}

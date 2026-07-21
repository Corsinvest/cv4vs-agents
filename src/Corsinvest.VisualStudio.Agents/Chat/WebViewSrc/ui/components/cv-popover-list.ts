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
 * skips both section headings and non-navigable rows (e.g. disabled models). Filtering (Fuse,
 * etc.) is NOT here — a searchable list emits `search-input` and the caller re-supplies items.
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
                padding: 6px 8px;
                border-radius: var(--borderRadiusMedium);
                cursor: pointer;
                color: var(--colorNeutralForeground2);
                font-family: var(--fontFamilyBase);
                font-size: 1em;
                line-height: var(--lineHeightBase300);
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
            .row.disabled {
                cursor: default;
                opacity: 0.55;
            }
            .row.disabled:hover {
                background: transparent;
                color: var(--colorNeutralForeground2);
            }

            /* --- Row-content classes shared by the callers' renderRow markup (they render into
             *     this component's shadow, so their styles must live here). --- */
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
                gap: 1px;
            }
            .row-desc {
                font-size: var(--fontSizeBase200);
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
            .dots-val {
                min-width: 3.5em;
                text-align: right;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                text-transform: capitalize;
            }
            .row.selected cv-segmented-slider {
                --cv-slider-border: var(--colorNeutralForegroundOnBrand);
            }
            .row.selected .dots-val,
            .row.selected .row-trailing {
                color: var(--colorNeutralForegroundOnBrand);
                opacity: 0.85;
            }
        `,
    ];

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
    @property({ type: Boolean }) searchable = false;
    @property() query = '';

    @state() private _activeIdx = 0;

    @query('.list') private _list?: HTMLDivElement;
    @query('.search') private _search?: HTMLElement & { value: string };

    /** The flat list of navigable items, in display order — what ↑/↓ and pickActive index into. */
    private get _nav(): unknown[] {
        const nav = this.isNavigable;
        return nav ? this.items.filter(nav) : this.items;
    }

    override willUpdate(changed: Map<string, unknown>): void {
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
            .join(' ');
    }
    private _lastNavSig = '';

    override updated(changed: Map<string, unknown>): void {
        if (changed.has('searchable') && this.searchable) {
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
        const cls = ['row', navigable ? 'navigable' : 'disabled', selected ? 'selected' : ''].join(
            ' ',
        );
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
                    ${s.items.map(rowOf)}
                `,
            )}`;
        }
        return html`${this.items.map(rowOf)}`;
    }

    override render() {
        const empty = this.sections
            ? this.sections.every((s) => s.items.length === 0)
            : this.items.length === 0;
        return html`
            <div class="popover">
                ${
                    this.searchable
                        ? html`<fluent-text-input
                              class="search"
                              type="text"
                              placeholder="Filter actions…"
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
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-popover-list': CvPopoverList;
    }
}

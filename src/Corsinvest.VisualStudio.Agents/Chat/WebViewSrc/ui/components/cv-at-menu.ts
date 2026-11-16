/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property, query } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { iconUrl, symbolIconUrl } from '../../core/icon-url';
import { fileName, mentionToken, normPath, relPath } from '../../core/path';
import type { AtItemDto, SymbolItemDto } from '../../core/types';
import './cv-popover-list';
import type { CvPopoverList } from './cv-popover-list';

export type AtTab = 'files' | 'symbols';

/**
 * Inline suggestion popover for `@` in `<cv-prompt>`, with two tabs: Files (the workspace listing)
 * and Symbols (a NavigateTo search). A thin wrapper over cv-popover-list: supplies the rows of the
 * current tab and a renderRow for each kind, and re-emits `select-at` (a file), `select-symbol` (a
 * symbol) and `at-tab` (a tab clicked). Parent-controlled: cv-prompt drives `open` and `tab` and
 * forwards keys via moveSelection()/pickActive().
 */
@customElement('cv-at-menu')
export class CvAtMenu extends LitElement {
    @property({ attribute: false }) items: AtItemDto[] = [];
    @property({ attribute: false }) symbolItems: SymbolItemDto[] = [];
    @property({ attribute: false }) tab: AtTab = 'files';
    @property({ attribute: false }) anchor: HTMLElement | null = null;
    @property({ type: Boolean, reflect: true }) open = false;
    // What the list says when it has no rows: "No matches", or why the host could not list the workspace.
    @property({ attribute: false }) emptyText = 'No matches';

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: pass-through wrapper, no styles of its own; cv-popover-list owns the shadow + CSS.
        return this;
    }

    // Parent (cv-prompt) drives navigation through these.
    moveSelection(delta: number): void {
        this._list?.moveSelection(delta);
    }
    movePage(dir: number): void {
        this._list?.movePage(dir);
    }
    pickActive(): void {
        this._list?.pickActive();
    }

    /** Folder shown next to the file name: path relative to the working directory, minus filename.
     *  Nothing for a directory row: its own label is the full path already. */
    private _dirLabel(it: AtItemDto): string {
        if (it.isDir) {
            return '';
        }
        if (it.dir) {
            return it.dir;
        }
        const rel = relPath(it.path, appState.workingDirectory);
        const slash = rel.lastIndexOf('/');
        return slash > 0 ? rel.slice(0, slash) : '';
    }

    private _pick(item: AtItemDto): void {
        const wd = appState.workingDirectory;
        const rel = relPath(item.path, wd);
        if (item.isDir) {
            // Trailing space, like a file: picking a folder finishes the token. It used to be left
            // open so the menu could be re-queried and walked into, which was the only way down
            // when the search stopped at the first level: the recursive one finds a file wherever
            // it sits, so the walk is gone and a folder is now a reference in its own right.
            const dirToken = rel && rel !== normPath(item.path) ? rel : fileName(item.path);
            // The trailing slash goes INSIDE the quotes: it is part of the path the CLI reads, and
            // outside them it would sit past the closing quote where nothing looks for it.
            this.dispatchEvent(
                new CustomEvent<{ token: string; isDir: true }>('select-at', {
                    detail: { token: `${mentionToken(`${dirToken}/`)} `, isDir: true },
                    bubbles: true,
                    composed: true,
                }),
            );
            return;
        }
        const replacement =
            rel && rel !== normPath(item.path) ? rel : item.name || fileName(item.path);
        this.dispatchEvent(
            new CustomEvent<{ token: string; isDir: false }>('select-at', {
                detail: { token: `${mentionToken(replacement)} `, isDir: false },
                bubbles: true,
                composed: true,
            }),
        );
    }

    private _setTab(tab: AtTab): void {
        if (tab !== this.tab) {
            this.dispatchEvent(
                new CustomEvent<{ tab: AtTab }>('at-tab', {
                    detail: { tab },
                    bubbles: true,
                    composed: true,
                }),
            );
        }
    }

    private _header() {
        // mousedown is cancelled like the rows': taking focus from the textarea would close the menu.
        const tab = (id: AtTab, label: string) => html`
            <button
                type="button"
                class="at-tab"
                aria-pressed=${this.tab === id ? 'true' : 'false'}
                @mousedown=${(e: MouseEvent) => e.preventDefault()}
                @click=${() => this._setTab(id)}
            >
                ${label}
            </button>
        `;
        return html`
            <span class="at-tabs" role="group" aria-label="Reference">
                ${tab('files', 'Files')}${tab('symbols', 'Symbols')}
            </span>
            <span class="section-hint">Ctrl + ←/→</span>
        `;
    }

    private _symbolTip(it: SymbolItemDto): string {
        return `${relPath(it.path, appState.workingDirectory)}:${it.line}`;
    }

    override render() {
        if (!this.open) {
            return html``;
        }
        if (this.tab === 'symbols') {
            return html`
                <cv-popover-list
                    .header=${this._header()}
                    .items=${this.symbolItems}
                    .emptyText=${this.emptyText}
                    .rowTip=${(it: SymbolItemDto) => this._symbolTip(it)}
                    .renderRow=${(it: SymbolItemDto) => html`
                        <span class="item-icon">
                            <img src=${symbolIconUrl(it.kind)} alt="" width="16" height="16" />
                        </span>
                        <span class="item-name">
                            ${it.name}
                            ${
                                it.container
                                    ? html`<span class="item-container">${it.container}</span>`
                                    : ''
                            }
                        </span>
                        <span class="item-dir">${fileName(it.path)}</span>
                    `}
                    @select=${(e: CustomEvent<{ item: SymbolItemDto }>) =>
                        this.dispatchEvent(
                            new CustomEvent<{ item: SymbolItemDto }>('select-symbol', {
                                detail: { item: e.detail.item },
                                bubbles: true,
                                composed: true,
                            }),
                        )}
                ></cv-popover-list>
            `;
        }
        return html`
            <cv-popover-list
                .header=${this._header()}
                .items=${this.items}
                .emptyText=${this.emptyText}
                .renderRow=${(it: AtItemDto) => html`
                    <span class="item-icon">
                        <img src=${iconUrl(it.path, !!it.isDir)} alt="" width="16" height="16" />
                    </span>
                    <span class="item-name">${it.name}</span>
                    <span class="item-dir">${this._dirLabel(it)}</span>
                `}
                @select=${(e: CustomEvent<{ item: AtItemDto }>) => this._pick(e.detail.item)}
            ></cv-popover-list>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-at-menu': CvAtMenu;
    }
}

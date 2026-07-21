/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property, query } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { iconUrl } from '../../core/icon-url';
import { fileName, normPath, relPath } from '../../core/path';
import type { AtItemDto } from '../../core/types';
import './cv-popover-list';
import type { CvPopoverList } from './cv-popover-list';

/**
 * Inline file-suggestion popover for `@` in `<cv-prompt>`. A thin wrapper over cv-popover-list: supplies
 * the (already-filtered) file items + a renderRow (icon + name + folder), and re-emits `select-at`.
 * Parent-controlled: cv-prompt drives `open` and forwards keys via moveSelection()/pickActive().
 */
@customElement('cv-at-menu')
export class CvAtMenu extends LitElement {
    @property({ attribute: false }) items: AtItemDto[] = [];
    @property({ attribute: false }) anchor: HTMLElement | null = null;
    @property({ type: Boolean, reflect: true }) open = false;

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: pass-through wrapper, no styles of its own; cv-popover-list owns the shadow + CSS.
        return this;
    }

    // Parent (cv-prompt) drives navigation through these.
    moveSelection(delta: number): void {
        this._list?.moveSelection(delta);
    }
    pickActive(): void {
        this._list?.pickActive();
    }

    /** Folder shown next to the file name: path relative to the working directory, minus filename. */
    private _dirLabel(it: AtItemDto): string {
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
            const dirToken = rel && rel !== normPath(item.path) ? rel : fileName(item.path);
            this.dispatchEvent(
                new CustomEvent<{ token: string; isDir: true }>('select-at', {
                    detail: { token: `@${dirToken}/`, isDir: true },
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
                detail: { token: `@${replacement} `, isDir: false },
                bubbles: true,
                composed: true,
            }),
        );
    }

    override render() {
        if (!this.open) {
            return html``;
        }
        return html`
            <cv-popover-list
                .items=${this.items}
                emptyText="No matches"
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

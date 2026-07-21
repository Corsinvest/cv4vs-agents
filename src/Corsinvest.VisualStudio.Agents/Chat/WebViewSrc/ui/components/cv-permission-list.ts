/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Checkmark16Regular from '@fluentui/svg-icons/icons/checkmark_16_regular.svg';
import { state as appState } from '../../core/state';
import { permissionItems, type PermissionItem } from '../../core/permission-modes';
import type { PermissionMode } from '../../core/types';
import './cv-popover-list';
import type { CvPopoverList } from './cv-popover-list';

/**
 * Permission-mode picker shown ABOVE the textarea, like the `/` command menu and the model
 * picker — the three "pick one from a list" menus now open the same way (an anchored popover
 * was the odd one out next to the model picker in the same toolbar). A thin wrapper over
 * cv-popover-list: supplies the modes + a renderRow (icon + label + description + a check on
 * the active one) and re-emits `select-permission`. Parent-controlled (cv-prompt): forwards
 * keys via moveSelection()/pickActive().
 */
@customElement('cv-permission-list')
export class CvPermissionList extends LitElement {
    @property({ type: Boolean, reflect: true }) open = false;

    @state() private _current: PermissionMode = appState.permissionMode;
    @state() private _items: PermissionItem[] = permissionItems();

    private _off?: () => void;
    private _offModels?: () => void;

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: this wrapper has no styles of its own; cv-popover-list owns the shadow + CSS.
        return this;
    }

    override connectedCallback(): void {
        super.connectedCallback();
        this._off = appState.on('permissionMode', (v) => {
            this._current = v;
        });
        // The available modes depend on the model (supportsAutoMode).
        this._offModels = appState.on('models', () => {
            this._items = permissionItems();
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._off?.();
        this._offModels?.();
    }

    override willUpdate(changed: Map<string, unknown>): void {
        if (changed.has('open') && this.open) {
            this._current = appState.permissionMode;
            this._items = permissionItems();
        }
    }

    override updated(changed: Map<string, unknown>): void {
        if (changed.has('open') && this.open) {
            const idx = this._items.findIndex((it) => it.value === this._current);
            this._list?.setActive(idx >= 0 ? idx : 0);
        }
    }

    // Parent (cv-prompt) drives navigation through these.
    moveSelection(delta: number): void {
        this._list?.moveSelection(delta);
    }
    pickActive(): void {
        this._list?.pickActive();
    }

    private _pick(it: PermissionItem): void {
        this.dispatchEvent(
            new CustomEvent<{ value: PermissionMode }>('select-permission', {
                detail: { value: it.value },
                bubbles: true,
                composed: true,
            }),
        );
    }

    override render() {
        if (!this.open) {
            return html``;
        }
        const cur = this._current;
        return html`
            <cv-popover-list
                .items=${this._items}
                .sections=${[
                    { label: 'Permission modes', hint: '⇧ + tab to switch', items: this._items },
                ]}
                emptyText="No modes"
                .renderRow=${(it: PermissionItem) => html`
                    <span class="row-icon">${unsafeHTML(it.icon)}</span>
                    <span class="row-text">
                        <span class="row-label">${it.label}</span>
                        <span class="row-desc">${it.description}</span>
                    </span>
                    ${
                        it.value === cur
                            ? html`<span class="row-check">${unsafeHTML(Checkmark16Regular)}</span>`
                            : nothing
                    }
                `}
                @select=${(e: CustomEvent<{ item: PermissionItem }>) => this._pick(e.detail.item)}
            ></cv-popover-list>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-permission-list': CvPermissionList;
    }
}

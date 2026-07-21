/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Checkmark16Regular from '@fluentui/svg-icons/icons/checkmark_16_regular.svg';
import { state as appState } from '../../core/state';
import { resolveModelValue } from '../../core/ai-models';
import type { ModelInfoDto } from '../../core/types';
import './cv-popover-list';
import type { CvPopoverList } from './cv-popover-list';

/**
 * Model picker shown ABOVE the textarea (like the `/` command menu), opened from the menu's
 * "Switch model…" row. A thin wrapper over cv-popover-list: supplies the models + a renderRow (name +
 * description + a check on the active one), marks disabled models non-navigable, and re-emits
 * `select-model`. Parent-controlled (cv-prompt): forwards keys via moveSelection()/pickActive().
 */
@customElement('cv-model-list')
export class CvModelList extends LitElement {
    @property({ type: Boolean, reflect: true }) open = false;

    @state() private _models = appState.models;
    @state() private _current = appState.currentModel;

    private _offModels?: () => void;
    private _offCurrent?: () => void;

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: this wrapper has no styles of its own; cv-popover-list owns the shadow + CSS.
        return this;
    }

    override connectedCallback(): void {
        super.connectedCallback();
        this._offModels = appState.on('models', (v) => {
            this._models = v;
        });
        this._offCurrent = appState.on('currentModel', (v) => {
            this._current = v;
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offModels?.();
        this._offCurrent?.();
    }

    override willUpdate(changed: Map<string, unknown>): void {
        // On open, resync from app state and put the cursor on the active model.
        if (changed.has('open') && this.open) {
            this._current = appState.currentModel;
            this._models = appState.models;
        }
    }

    override updated(changed: Map<string, unknown>): void {
        if (changed.has('open') && this.open) {
            const active = resolveModelValue(this._current);
            const idx = this._models
                .filter((m) => !m.disabled)
                .findIndex((m) => m.value === active);
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

    private _pick(m: ModelInfoDto): void {
        if (m.disabled) {
            return;
        }
        this.dispatchEvent(
            new CustomEvent<{ value: string }>('select-model', {
                detail: { value: m.value },
                bubbles: true,
                composed: true,
            }),
        );
    }

    override render() {
        if (!this.open) {
            return html``;
        }
        const active = resolveModelValue(this._current);
        return html`
            <cv-popover-list
                .items=${this._models}
                .isNavigable=${(m: ModelInfoDto) => !m.disabled}
                .sections=${[{ label: 'Select a model', items: this._models }]}
                emptyText="No models"
                .renderRow=${(m: ModelInfoDto) => html`
                    <span class="row-text">
                        <span class="row-label">${m.displayName}</span>
                        <span class="row-desc">${m.description}</span>
                    </span>
                    ${
                        !m.disabled && m.value === active
                            ? html`<span class="row-check">${unsafeHTML(Checkmark16Regular)}</span>`
                            : nothing
                    }
                `}
                @select=${(e: CustomEvent<{ item: ModelInfoDto }>) => this._pick(e.detail.item)}
            ></cv-popover-list>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-model-list': CvModelList;
    }
}

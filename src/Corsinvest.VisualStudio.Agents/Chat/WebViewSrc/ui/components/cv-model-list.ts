/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Checkmark16Regular from '@fluentui/svg-icons/icons/checkmark_16_regular.svg';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import { resolveModelValue } from '../../core/ai-models';
import type { ChatCommand, CommandHost } from '../../core/commands/base';
import { EffortCommand, UltracodeCommand } from '../../core/commands/model-controls';
import type { ModelInfoDto } from '../../core/types';
import './cv-popover-list';
import './cv-segmented-slider';
import type { SliderStop } from './cv-segmented-slider';
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
    /** The command host (cv-prompt), which the effort slider applies its level through. */
    @property({ attribute: false }) host!: CommandHost;

    // The same command the `/` menu's Effort row uses: its stops, label and setter follow the
    // current model's levels, so the two can't disagree.
    private readonly _effort = new EffortCommand();
    private readonly _ultracode = new UltracodeCommand();

    // `default` is listed even when another entry resolves to the same model: it follows the
    // CLI's recommendation when that changes, a named entry stays put.
    @state() private _models = appState.models;
    @state() private _current = appState.currentModel;

    private readonly _subs = new StateSubscriptions(this);

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: this wrapper has no styles of its own; cv-popover-list owns the shadow + CSS.
        return this;
    }

    constructor() {
        super();
        this._subs.on('models', (v) => {
            this._models = v;
        });
        this._subs.on('currentModel', (v) => {
            this._current = v;
        });
    }

    override willUpdate(changed: Map<string, unknown>): void {
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
    movePage(dir: number): void {
        this._list?.movePage(dir);
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

    private _renderLabel(cmd: ChatCommand) {
        return html`<span class="footer-label" data-tip=${cmd.description ?? ''}>
            ${cmd.icon ? html`<span class="row-icon">${unsafeHTML(cmd.icon)}</span>` : nothing}
            <span>${cmd.label}</span>
        </span>`;
    }

    /** Effort for the current model, below the list; absent when the model has none (Haiku). */
    private _renderEffort() {
        if (!this._effort.isEnabled()) {
            return nothing;
        }
        const ctrl = this._effort.trailingControl;
        if (ctrl.kind !== 'slider') {
            return nothing;
        }
        return html`<div class="footer-row">
            ${this._renderLabel(this._effort)}
            <span class="dots-wrap">
                <span class="dots-val">${ctrl.label}</span>
                <cv-segmented-slider
                    .stops=${ctrl.stops}
                    .activeValue=${ctrl.value}
                    @change=${(e: CustomEvent<SliderStop<number>>) => {
                        ctrl.onSet(this.host, e.detail.value);
                        this.requestUpdate();
                    }}
                ></cv-segmented-slider>
            </span>
        </div>`;
    }

    private _renderUltracode() {
        if (!this._ultracode.isEnabled()) {
            return nothing;
        }
        return html`<div class="footer-row">
            ${this._renderLabel(this._ultracode)}
            <fluent-switch
                class="toggle"
                aria-label=${this._ultracode.label}
                ?checked=${this._ultracode.checked}
                @change=${() => {
                    this._ultracode.run(this.host);
                    this.requestUpdate();
                }}
            ></fluent-switch>
        </div>`;
    }

    /** Undefined when the model has no effort: the list then draws no empty band. */
    private _renderFooter() {
        return this._effort.isEnabled()
            ? html`<div class="footer-rows">${this._renderEffort()}${this._renderUltracode()}</div>`
            : undefined;
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
                .header=${html`<span>Select a model</span>`}
                .footer=${this._renderFooter()}
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

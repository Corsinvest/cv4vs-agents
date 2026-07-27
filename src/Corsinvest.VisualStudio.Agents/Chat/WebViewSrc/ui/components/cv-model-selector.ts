/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// Same icon the menu's "Switch model…" row uses — one glyph for one concept.
import Brain16Regular from '@fluentui/svg-icons/icons/brain_16_regular.svg';
import { state as appState } from '../../core/state';
import { iconStyles } from '../styles/shared';
import { modelLabel, modelLabelShort, resolveModelValue } from '../../core/ai-models';
import { SwitchModelCommand } from '../../core/commands/builtin-commands';

/**
 * Model trigger in the input toolbar, next to the permission selector: shows the active model and
 * asks cv-prompt to open the picker (cv-model-list, above the textarea). The list, not this button,
 * owns the menu — same split as cv-permission-selector.
 *
 * The label is deliberately the SHORT name: the toolbar row is narrow and already carries attach,
 * slash, gauge, sub-agent, IDE badge, permission, mic and send. The full name and the model's
 * description go in the title, where there is no space pressure.
 */
@customElement('cv-model-selector')
export class CvModelSelector extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* Trigger is a <fluent-button> — keep it pure (layout only). */
            .trigger svg {
                width: 16px;
                height: 16px;
                margin-right: 4px;
            }
            /* A provider can return a long id as the display name; cap it rather than
               letting the toolbar reflow. */
            .trigger span {
                max-width: 14ch;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
        `,
    ];

    @state() private _current = appState.currentModel;
    @state() private _models = appState.models;

    // The menu row for the same thing: its description is the one place that says what picking a
    // model does, so the tooltip borrows it instead of wording it a second time.
    private readonly _command = new SwitchModelCommand();

    private _off?: () => void;
    private _offModels?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._off = appState.on('currentModel', (v) => {
            this._current = v;
        });
        // The label comes from the catalogue, so it resolves only once that has arrived.
        this._offModels = appState.on('models', (v) => {
            this._models = v;
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._off?.();
        this._offModels?.();
    }

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-models', { bubbles: true, composed: true }));
    };

    override render() {
        // Read so the label re-resolves when the catalogue lands (it maps value → displayName).
        void this._models;
        const full = modelLabel(this._current);
        // Full name + the CLI's own description of THIS model ("Opus 5 with 1M context · Best for
        // everyday, complex tasks"), then the command's line for what clicking does. The button has
        // no room for any of it; the tooltip has plenty.
        const info = this._models.find((m) => m.value === resolveModelValue(this._current));
        const tip = [full, info?.description, this._command.description].filter(Boolean).join('\n');
        return html`
            <fluent-button
                class="trigger"
                appearance="subtle"
                size="small"
                title=${tip}
                @click=${this._onClick}
            >
                ${unsafeHTML(Brain16Regular)}
                <span>${modelLabelShort(this._current)}</span>
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-model-selector': CvModelSelector;
    }
}

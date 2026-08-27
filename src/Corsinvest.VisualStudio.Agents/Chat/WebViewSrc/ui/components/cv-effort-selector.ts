/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { tooltipStyles } from '../styles/shared';
import { currentEffortLevels } from '../../core/commands/model-controls';
import { effortLabel } from '../../core/types';

/**
 * Effort trigger in the input toolbar, beside the model selector: shows the active reasoning effort
 * and asks cv-prompt to open the command palette on its Effort row — the same row, with the same
 * slider, that the palette shows when opened in full. The list, not this button, owns the control:
 * the same split as the model and permission triggers.
 *
 * Hidden when the model has no effort levels (Haiku), on the same condition that hides the menu row.
 */
@customElement('cv-effort-selector')
export class CvEffortSelector extends LitElement {
    static override styles = [
        tooltipStyles,
        css`
            :host {
                display: contents;
            }
            /* See cv-model-selector: Fluent's own subtle button, ours only the metrics. */
            .trigger {
                font-size: var(--fontSizeBase200);
                padding-inline: 8px;
                min-width: 0;
            }
        `,
    ];

    @state() private _effort = appState.effortLevel;
    @state() private _ultracode = appState.ultracodeEnabled;

    private _offs: Array<() => void> = [];

    override connectedCallback(): void {
        super.connectedCallback();
        this._offs = [
            appState.on('effortLevel', (v) => {
                this._effort = v;
            }),
            appState.on('ultracodeEnabled', (v) => {
                this._ultracode = v;
            }),
            appState.on('models', () => this.requestUpdate()),
            appState.on('currentModel', () => this.requestUpdate()),
        ];
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offs.forEach((off) => off());
        this._offs = [];
    }

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-effort', { bubbles: true, composed: true }));
    };

    override render() {
        if (currentEffortLevels() === null) {
            return nothing;
        }
        const label = effortLabel(this._effort, this._ultracode);
        return html`
            <fluent-button
                id="effort-trigger"
                class="trigger"
                aria-label="Effort"
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span>${label}</span>
            </fluent-button>
            <!-- The button already shows the level; the tooltip only has to say what the word is
                 the level OF. -->
            <fluent-tooltip anchor="effort-trigger" positioning="above-end">Effort</fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-effort-selector': CvEffortSelector;
    }
}

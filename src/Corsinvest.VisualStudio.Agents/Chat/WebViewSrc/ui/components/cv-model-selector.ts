/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { iconStyles, tooltipStyles } from '../styles/shared';
import { modelLabelShort } from '../../core/ai-models';

/**
 * Model trigger in the input toolbar, next to the permission selector: shows the active model and
 * asks cv-prompt to open the picker (cv-model-list, above the textarea). The list, not this button,
 * owns the menu — same split as cv-permission-selector.
 *
 * The label is deliberately the SHORT name: the toolbar row is narrow and already carries attach,
 * gauge, sub-agent, IDE badge, permission and mic. The full name and the model's description are
 * in the list a click opens, where there is no space pressure.
 */
@customElement('cv-model-selector')
export class CvModelSelector extends LitElement {
    static override styles = [
        iconStyles,
        tooltipStyles,
        css`
            :host {
                display: contents;
            }
            /* A provider can return a long id as the display name; cap it rather than
               letting the toolbar reflow. */
            /* Flat at rest, relief on hover — appearance="subtle" is Fluent's own, so the hover,
               pressed and focus states come with it in either theme rather than being written out
               here. No shape either: Fluent only has rules for circular and square, so the plain
               button already carries the radius size="small" gives it. No caret: this shows a
               value, and a value reads as chosen, therefore changeable. Only the metrics are ours —
               even size="small" is padded for a control standing alone, and min-width holds a floor
               a word like "Opus" never reaches. Both live on the host (the template is a single
               content span), so no ::part is involved. */
            .trigger {
                font-size: var(--fontSizeBase200);
                padding-inline: 8px;
                min-width: 0;
            }
            .trigger span {
                max-width: 14ch;
                overflow: hidden;
                text-overflow: ellipsis;
            }
        `,
    ];

    @state() private _current = appState.currentModel;
    @state() private _models = appState.models;

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
        // fluent-tooltip, not a title attribute: the native one is drawn by the OS, so it follows
        // Windows' light/dark rather than the theme VS is in — and VS has themes (Blue, third-party
        // ones) that neither of the two settings a WebView2 profile can be put in would match.
        // Same reason the gauge uses one.
        return html`
            <fluent-button
                id="model-trigger"
                class="trigger"
                aria-label="Model"
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span>${modelLabelShort(this._current)}</span>
            </fluent-button>
            <!-- The name of the control, like the permission trigger beside it. The full name, the
                 [1m] variant and the description are all in cv-model-list, one row each — a click
                 answers "which model is this exactly" better than a tooltip echoing the button. -->
            <fluent-tooltip anchor="model-trigger" positioning="above-end">Model</fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-model-selector': CvModelSelector;
    }
}

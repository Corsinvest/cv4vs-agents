/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import { iconStyles } from '../styles/shared';
import { modelLabelShort } from '../../core/ai-models';
import { currentEffortLevels, ultracodeActive } from '../../core/commands/model-controls';
import { effortLabel, ULTRACODE_LABEL } from '../../core/types';

/**
 * Model trigger in the input toolbar, next to the permission selector: shows the active model and
 * its effort, and asks cv-prompt to open the picker (cv-model-list, above the textarea), which
 * carries the effort slider below the models. The list, not this button, owns the menu: same split
 * as cv-permission-selector. The effort is left out when the model has none (Haiku), on the same
 * condition that hides the slider.
 *
 * The label is deliberately the SHORT name: the toolbar row is narrow and already carries attach,
 * gauge, sub-agent, IDE badge, permission and mic. The full name and the model's description are
 * in the list a click opens, where there is no space pressure.
 */
@customElement('cv-model-selector')
export class CvModelSelector extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* A provider can return a long id as the display name; cap it rather than
               letting the toolbar reflow. */
            /* Flat at rest, relief on hover: appearance="subtle" is Fluent's own, so the hover,
               pressed and focus states come with it in either theme rather than being written out
               here. No shape either: Fluent only has rules for circular and square, so the plain
               button already carries the radius size="small" gives it. No caret: this shows a
               value, and a value reads as chosen, therefore changeable. Only the metrics are ours:
               even size="small" is padded for a control standing alone, and min-width holds a floor
               a word like "Opus" never reaches. Both live on the host (the template is a single
               content span), so no ::part is involved. */
            .trigger {
                font-size: var(--fontSizeBase200);
                padding-inline: 8px;
                min-width: 0;
            }
            .model {
                max-width: 14ch;
                overflow: hidden;
                text-overflow: ellipsis;
            }
            /* A soft tag, not plain grey text: as text the level ran into the permission trigger
               beside it ("Opus 5.5 Medium Manual"), and a border on the button would be the only
               one in the toolbar. A span of ours, so the fill is allowed. */
            .effort {
                margin-inline-start: 4px;
                padding: 1px 6px;
                border-radius: var(--borderRadiusMedium);
                background: var(--colorNeutralBackground3);
                color: var(--colorNeutralForeground3);
                white-space: nowrap;
            }
            /* Inside the level's tag, set apart by colour alone: it is on for every task until
               turned off, so it has to show with the menu closed, without a second box that
               outweighs the model's name. */
            .ultracode {
                color: var(--colorPaletteBerryBorderActive);
            }
        `,
    ];

    @state() private _current = appState.currentModel;
    @state() private _effort = appState.effortLevel;

    private readonly _subs = new StateSubscriptions(this);

    constructor() {
        super();
        this._subs.on('currentModel', (v) => {
            this._current = v;
        });
        this._subs.on('effortLevel', (v) => {
            this._effort = v;
        });
        this._subs.rerenderOn('models', 'ultracodeEnabled');
    }

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-models', { bubbles: true, composed: true }));
    };

    override render() {
        // data-tip names the control, like the permission trigger beside it. The full name, the
        // [1m] variant and the description are all in cv-model-list, one row each; a click answers
        // "which model is this exactly" better than a tooltip echoing the button.
        return html`
            <fluent-button
                id="model-trigger"
                class="trigger"
                aria-label="Model and effort"
                data-tip="Model and effort"
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span class="model">${modelLabelShort(this._current)}</span>
                ${
                    currentEffortLevels() !== null
                        ? html`<span class="effort"
                              >${effortLabel(this._effort)}${
                                  ultracodeActive()
                                      ? html` · <span class="ultracode">${ULTRACODE_LABEL}</span>`
                                      : nothing
                              }</span
                          >`
                        : nothing
                }
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-model-selector': CvModelSelector;
    }
}

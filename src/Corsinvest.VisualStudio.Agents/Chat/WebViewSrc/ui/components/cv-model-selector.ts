/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Rocket16Filled from '@fluentui/svg-icons/icons/rocket_16_filled.svg';
import Lightbulb16Filled from '@fluentui/svg-icons/icons/lightbulb_16_filled.svg';
import AnimalRabbit16Filled from '@fluentui/svg-icons/icons/animal_rabbit_16_filled.svg';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import { iconStyles } from '../styles/shared';
import { modelLabelShort } from '../../core/ai-models';
import {
    currentEffortLevels,
    FastModeCommand,
    ThinkingCommand,
    UltracodeCommand,
} from '../../core/commands/model-controls';
import { effortLabel } from '../../core/types';

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
            /* The picker's switches that are on, as their icons inside the level's tag: they stay
               on until turned off, so they have to show with the menu closed, without a second box
               that outweighs the model's name. On Haiku (no level) the tag holds only these. */
            .flag {
                display: inline-flex;
                vertical-align: -2px;
            }
            .level + .flag,
            .flag + .flag {
                margin-inline-start: 4px;
            }
            .flag svg {
                width: 12px;
                height: 12px;
            }
            /* Set apart by colour: it changes what every task does, the other two only how. */
            .ultracode {
                color: var(--colorPaletteBerryBorderActive);
            }
        `,
    ];

    @state() private _current = appState.currentModel;
    @state() private _effort = appState.effortLevel;

    // The picker's switches, read here for their state only: an icon shows while one is on. Filled,
    // not the menu's regular glyph: at 12px the outline is too thin to tell apart.
    private readonly _flags = [
        { cmd: new UltracodeCommand(), icon: Rocket16Filled },
        { cmd: new ThinkingCommand(), icon: Lightbulb16Filled },
        { cmd: new FastModeCommand(), icon: AnimalRabbit16Filled },
    ];

    private readonly _subs = new StateSubscriptions(this);

    constructor() {
        super();
        this._subs.on('currentModel', (v) => {
            this._current = v;
        });
        this._subs.on('effortLevel', (v) => {
            this._effort = v;
        });
        this._subs.rerenderOn('models', 'ultracodeEnabled', 'thinkingEnabled', 'fastMode');
    }

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-models', { bubbles: true, composed: true }));
    };

    override render() {
        // data-tip names the control, like the permission trigger beside it. The full name, the
        // [1m] variant and the description are all in cv-model-list, one row each; a click answers
        // "which model is this exactly" better than a tooltip echoing the button. The flags are
        // icons only, so the tip is where they are named.
        const hasEffort = currentEffortLevels() !== null;
        const on = this._flags.filter((f) => f.cmd.checked && f.cmd.isEnabled());
        const tip =
            on.length > 0
                ? `Model and effort: ${on.map((f) => f.cmd.label).join(', ')}`
                : 'Model and effort';
        return html`
            <fluent-button
                id="model-trigger"
                class="trigger"
                aria-label=${tip}
                data-tip=${tip}
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span class="model">${modelLabelShort(this._current)}</span>
                ${
                    hasEffort || on.length > 0
                        ? html`<span class="effort"
                              >${
                                  hasEffort
                                      ? html`<span class="level"
                                            >${effortLabel(this._effort)}</span
                                        >`
                                      : nothing
                              }${on.map(
                                  (f) =>
                                      html`<span
                                          class="flag ${
                                              f.cmd instanceof UltracodeCommand ? 'ultracode' : ''
                                          }"
                                          >${unsafeHTML(f.icon)}</span
                                      >`,
                              )}</span
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// The menu's own Effort glyph, shown in the panel header.
import Dumbbell16Regular from '@fluentui/svg-icons/icons/dumbbell_16_regular.svg';
import { state as appState } from '../../core/state';
import { iconStyles, iconButtonStyles, tooltipStyles } from '../styles/shared';
import { currentEffortLevels, EffortCommand } from '../../core/commands/model-controls';
import type { CommandHost } from '../../core/commands/base';
import { effortLabel } from '../../core/types';
import './cv-segmented-slider';
import type { SliderStop } from './cv-segmented-slider';

/**
 * Effort trigger in the input toolbar, beside the model selector: shows the active reasoning effort
 * and opens a small popover holding the same `cv-segmented-slider` the menu's Effort row uses.
 *
 * A popover rather than "open the command palette on that row": the palette is forty rows deep for
 * one setting, and it would have to scroll to and highlight the right one. The neighbouring model
 * and permission triggers open their own list too, so this keeps the toolbar consistent.
 *
 * The stops and the set logic come from EffortCommand — ultracode is a special case in three places
 * (a synthetic stop, effort=xhigh plus a flag, cleared by any other stop) and must not be written
 * twice. This component is presentation only.
 *
 * Hidden when the model has no effort levels (Haiku), on the same condition that hides the menu row.
 */
@customElement('cv-effort-selector')
export class CvEffortSelector extends LitElement {
    static override styles = [
        iconStyles,
        iconButtonStyles,
        tooltipStyles,
        css`
            /* Relative host so the popover can sit above the chip — plain absolute positioning,
               like cv-context-gauge (CSS anchor-positioning misplaces top-layer popovers in the
               VS WebView2's Chromium). */
            :host {
                position: relative;
                display: inline-flex;
            }
            .popover {
                position: absolute;
                bottom: calc(100% + 4px);
                /* Anchored right: the chip sits in the row's right-hand group, so a panel growing
                   leftwards stays inside the composer. Anchored left it would run off the right
                   edge, and the chip's own width changes with the level's name. */
                right: 0;
                z-index: 1000;
                padding: 8px 10px;
                display: flex;
                flex-direction: column;
                gap: 6px;
                white-space: nowrap;
                background: var(--colorNeutralBackground1);
                color: var(--colorNeutralForeground1);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                box-shadow: var(--shadow16);
            }
            .popover[hidden] {
                display: none;
            }
            /* Header: says what the slider regulates — on its own the control is unlabelled. */
            .head {
                display: flex;
                align-items: baseline;
                justify-content: space-between;
                gap: 12px;
                font-size: var(--fontSizeBase200);
            }
            .head-title {
                display: inline-flex;
                align-items: center;
                gap: 5px;
                font-weight: var(--fontWeightSemibold);
                color: var(--colorNeutralForeground1);
            }
            .head-title svg {
                width: 14px;
                height: 14px;
            }
            /* The active stop's name. Right-aligned in a fixed box so stepping through the levels
               doesn't resize the popover under the pointer. */
            .value {
                color: var(--colorNeutralForeground2);
                min-width: 10ch;
                text-align: right;
            }
        `,
    ];

    /** The command host (cv-prompt), which owns applyFlagSettings. Passed as a property because
     *  cv-prompt renders into a shadow root: `closest()` would stop at our own boundary. Same way
     *  cv-command-menu receives it. */
    @property({ attribute: false }) host!: CommandHost;

    @state() private _open = false;
    @state() private _effort = appState.effortLevel;
    @state() private _ultracode = appState.ultracodeEnabled;
    // The catalogue decides whether this model has effort at all.
    @state() private _models = appState.models;
    @state() private _current = appState.currentModel;

    private _offs: Array<() => void> = [];
    private readonly _command = new EffortCommand();

    override connectedCallback(): void {
        super.connectedCallback();
        this._offs = [
            appState.on('effortLevel', (v) => {
                this._effort = v;
            }),
            appState.on('ultracodeEnabled', (v) => {
                this._ultracode = v;
            }),
            appState.on('models', (v) => {
                this._models = v;
            }),
            appState.on('currentModel', (v) => {
                this._current = v;
            }),
        ];
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offs.forEach((off) => off());
        this._offs = [];
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
    }

    /** Light dismiss. composedPath crosses the shadow boundary, so a click on our own
     *  trigger is "inside" and leaves the toggle to handle it. */
    private _onDocPointerDown = (e: PointerEvent): void => {
        if (this._open && !e.composedPath().includes(this)) {
            this._open = false;
        }
    };

    private _toggle = (): void => {
        this._open = !this._open;
    };

    override render() {
        // Read so the availability check re-runs when either changes.
        void this._models;
        void this._current;
        if (currentEffortLevels() === null) {
            return nothing;
        }
        const label = effortLabel(this._effort, this._ultracode);
        const ctrl = this._command.trailingControl;
        return html`
            <button id="effort-trigger" class="icon-btn" type="button" @click=${this._toggle}>
                <span>${label}</span>
            </button>
            <fluent-tooltip anchor="effort-trigger" positioning="above-end">
                <span class="tip-name">${this._command.label}: ${label}</span>
                <span class="tip-action">${this._command.description}</span>
            </fluent-tooltip>
            <div class="popover" ?hidden=${!this._open}>
                <div class="head">
                    <!-- The glyph lives here now that the trigger is a word-only pill: the panel
                         has the room the toolbar row hasn't, and it's the menu's own Effort icon. -->
                    <span class="head-title">${unsafeHTML(Dumbbell16Regular)} Effort</span>
                    <span class="value">${label}</span>
                </div>
                <cv-segmented-slider
                    .stops=${ctrl.kind === 'slider' ? ctrl.stops : []}
                    .activeValue=${ctrl.kind === 'slider' ? ctrl.value : 0}
                    @change=${(e: CustomEvent<SliderStop<number>>) => {
                        if (this.host && ctrl.kind === 'slider') {
                            ctrl.onSet(this.host, e.detail.value);
                        }
                    }}
                ></cv-segmented-slider>
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-effort-selector': CvEffortSelector;
    }
}

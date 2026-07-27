/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// Filled when on, regular when off: the shape carries the state, so it still reads on a poor
// display or for someone who can't rely on the colour.
import Lightbulb16Filled from '@fluentui/svg-icons/icons/lightbulb_16_filled.svg';
import Lightbulb16Regular from '@fluentui/svg-icons/icons/lightbulb_16_regular.svg';
import { state as appState } from '../../core/state';
import { iconStyles } from '../styles/shared';
import { ThinkingCommand } from '../../core/commands/model-controls';
import type { CommandHost } from '../../core/commands/base';

/**
 * Thinking toggle in the composer's settings row. Icon only: thinking is on or off, so unlike the
 * effort and model selectors there is no value worth spelling out beside the glyph.
 *
 * Wraps ThinkingCommand — the same object the menu's Thinking row uses — so the two can never
 * disagree about the label, the description, when the toggle applies, or what gets sent to the CLI.
 *
 * Note this state is per-process, not persisted: the CLI takes it over set_max_thinking_tokens and
 * a reopened pane goes back to what settings.json says.
 */
@customElement('cv-thinking-toggle')
export class CvThinkingToggle extends LitElement {
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
            }
        `,
    ];

    /** cv-prompt, which owns setMaxThinkingTokens. A property because cv-prompt renders into a
     *  shadow root — the same way cv-command-menu and cv-effort-selector receive it. */
    @property({ attribute: false }) host!: CommandHost;

    @state() private _enabled = appState.thinkingEnabled;
    // The catalogue decides whether this model thinks at all.
    @state() private _models = appState.models;
    @state() private _current = appState.currentModel;

    private _offs: Array<() => void> = [];
    private readonly _command = new ThinkingCommand();

    override connectedCallback(): void {
        super.connectedCallback();
        this._offs = [
            appState.on('thinkingEnabled', (v) => {
                this._enabled = v;
            }),
            appState.on('models', (v) => {
                this._models = v;
            }),
            appState.on('currentModel', (v) => {
                this._current = v;
            }),
        ];
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offs.forEach((off) => off());
        this._offs = [];
    }

    private _onClick = (): void => {
        if (this.host) {
            this._command.run(this.host);
        }
    };

    override render() {
        // Read so the availability check re-runs when either changes.
        void this._models;
        void this._current;
        // Hidden on models without adaptive thinking, on the same condition as the menu row.
        if (!this._command.isEnabled()) {
            return nothing;
        }
        const on = this._enabled;
        return html`
            <fluent-button
                class="trigger"
                appearance="subtle"
                size="small"
                icon-only
                aria-pressed=${on ? 'true' : 'false'}
                title=${`${this._command.label}: ${on ? 'on' : 'off'}\n${this._command.description}`}
                @click=${this._onClick}
            >
                ${unsafeHTML(on ? Lightbulb16Filled : Lightbulb16Regular)}
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-thinking-toggle': CvThinkingToggle;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import SlashForward16Regular from '@fluentui/svg-icons/icons/slash_forward_16_regular.svg';
import { iconStyles, tooltipStyles } from '../styles/shared';

/**
 * Slash button in the input toolbar. Opens the unified command palette
 * (cv-command-menu) showing every section — the same menu the `/` trigger
 * opens (filtered). Emits `open-commands`; cv-prompt owns the menu.
 * Shadow DOM + static styles; iconStyles fills the SVG.
 */
@customElement('cv-slash-menu')
export class CvSlashMenu extends LitElement {
    static override styles = [
        iconStyles,
        tooltipStyles,
        css`
            /* Fluent's own button, cut to the row's density: even size="small" is padded for a
               control standing alone. No accent colour — see cv-attach-menu: in this row colour
               means state, and this button has none. */
            .trigger {
                padding: 3px;
                min-width: 0;
            }
        `,
    ];

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-commands', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            <fluent-button
                id="slash-trigger"
                class="trigger"
                appearance="subtle"
                shape="rounded"
                size="small"
                icon-only
                @click=${this._onClick}
            >
                ${unsafeHTML(SlashForward16Regular)}
            </fluent-button>
            <fluent-tooltip anchor="slash-trigger" positioning="above-start">
                <span class="tip-name">Commands</span>
                <span class="tip-action">Every slash command, in one list</span>
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-slash-menu': CvSlashMenu;
    }
}

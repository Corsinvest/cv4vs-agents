/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import SlashForward16Regular from '@fluentui/svg-icons/icons/slash_forward_16_regular.svg';
import { iconStyles } from '../styles/shared';

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
        css`
            /* 16px slash glyph, marigold "system" accent (like the model/permission triggers). */
            svg {
                width: 16px;
                height: 16px;
                color: var(--colorPaletteMarigoldForeground1);
            }
        `,
    ];

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-commands', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            <fluent-button
                appearance="subtle"
                size="small"
                icon-only
                title="Commands"
                @click=${this._onClick}
            >
                ${unsafeHTML(SlashForward16Regular)}
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-slash-menu': CvSlashMenu;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import SlashForward16Regular from '@fluentui/svg-icons/icons/slash_forward_16_regular.svg';
import { iconStyles, iconButtonStyles, tooltipStyles } from '../styles/shared';

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
        iconButtonStyles,
        tooltipStyles,
        css`
            /* Marigold "system" accent (like the model/permission triggers), at the shared 14px. */
            .icon-btn {
                color: var(--colorPaletteMarigoldForeground1);
            }
        `,
    ];

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-commands', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            <button id="slash-trigger" class="icon-btn" type="button" @click=${this._onClick}>
                ${unsafeHTML(SlashForward16Regular)}
            </button>
            <fluent-tooltip anchor="slash-trigger" positioning="above-start"
                >Commands</fluent-tooltip
            >
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-slash-menu': CvSlashMenu;
    }
}

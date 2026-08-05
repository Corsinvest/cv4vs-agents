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
            <!-- positioning=before, not the above-start the triggers on the right use: this one
                 sits against the textarea, and anything opening above it lands on the placeholder.
                 There is no room on the inline-start side either, so Fluent flips it to the other
                 side — which is the point: beside the button rather than over the text.
                 tip-desc, not tip-action: alone, the smaller action size reads as a footnote to
                 something that isn't there. -->
            <fluent-tooltip anchor="slash-trigger" positioning="before">
                <span class="tip-desc">Every slash command, in one list</span>
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-slash-menu': CvSlashMenu;
    }
}

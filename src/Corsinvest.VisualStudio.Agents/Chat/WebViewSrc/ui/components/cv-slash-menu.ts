/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import SlashForward16Regular from '@fluentui/svg-icons/icons/slash_forward_16_regular.svg';
import { iconStyles, iconTriggerStyles, tooltipStyles } from '../styles/shared';

/**
 * Slash button in the input toolbar. Opens the unified command palette
 * (cv-command-menu) showing every section — the same menu the `/` trigger
 * opens (filtered). Emits `open-commands`; cv-prompt owns the menu.
 * Shadow DOM + static styles; iconStyles fills the SVG.
 */
@customElement('cv-slash-menu')
export class CvSlashMenu extends LitElement {
    static override styles = [iconStyles, iconTriggerStyles, tooltipStyles];

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
            <!-- positioning=before, not above-start: this trigger sits against the textarea, and
                 anything opening above it lands on the placeholder. -->
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

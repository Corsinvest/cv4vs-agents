/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { iconStyles } from '../styles/shared';

/**
 * Attachment chip: an image icon + a name, optionally removable. Purely
 * presentational — whoever creates it knows the action, so the chip has no
 * semantics: the click on the chip is native (bubbles to the host; the creator
 * binds @click to open the lightbox / VS file / IDE file). The only custom event
 * is `remove` (the ✕, when removable). `accent='brand'` gives the IDE-ref look.
 * Shadow DOM.
 */
@customElement('cv-attach-chip')
export class CvAttachChip extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                /* Sized to its content; the remove ✕ overlays the corner on hover. */
                position: relative;
                display: inline-flex;
                align-items: center;
                gap: 6px;
                background: var(--colorNeutralBackground3);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusSmall);
                padding: 3px 8px 3px 4px;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                max-width: 280px;
                cursor: pointer;
            }
            /* IDE-ref look: brand-blue border + link text. */
            :host([accent='brand']) {
                border-color: var(--colorBrandStroke1);
                color: var(--colorBrandForegroundLink);
                opacity: 0.85;
            }
            :host([accent='brand']:hover) {
                opacity: 1;
            }
            .icon {
                width: 16px;
                height: 16px;
                object-fit: cover;
                border-radius: 2px;
                display: block;
                flex-shrink: 0;
            }
            .label {
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            /* Remove ✕: floats over the top-right corner, only on hover — no inline
             * space reserved, so the chip stays as wide as the filename. */
            .remove {
                position: absolute;
                top: -6px;
                right: -6px;
                display: inline-flex;
                align-items: center;
                justify-content: center;
                width: 16px;
                height: 16px;
                padding: 0;
                border: none;
                border-radius: 50%;
                background: var(--colorNeutralBackground1);
                color: var(--colorNeutralForeground2);
                box-shadow: 0 0 0 1px var(--colorNeutralStroke1);
                cursor: pointer;
                line-height: 1;
                opacity: 0;
                transition: opacity 0.15s;
            }
            .remove svg {
                width: 12px;
                height: 12px;
            }
            :host(:hover) .remove,
            :host(:focus-within) .remove {
                opacity: 1;
            }
            .remove:hover {
                color: var(--colorPaletteRedForeground1);
            }
        `,
    ];

    @property() src = '';
    @property() label = '';
    @property({ reflect: true }) accent?: 'brand';
    @property({ type: Boolean }) removable = false;

    private _remove = (e: Event): void => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('remove', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            ${this.src ? html`<img class="icon" src=${this.src} alt="" />` : nothing}
            <span class="label">${this.label}</span>
            ${
                this.removable
                    ? html`<button class="remove" title="Remove" @click=${this._remove}>
                          ${unsafeHTML(Dismiss16Regular)}
                      </button>`
                    : nothing
            }
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-attach-chip': CvAttachChip;
    }
}

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
 * presentational: whoever creates it knows the action, so the chip has no
 * semantics: the click on the chip is native (bubbles to the host; the creator
 * binds @click to open the lightbox / VS file / IDE file). The only custom event
 * is `remove` (the ✕, when removable).
 * Shadow DOM.
 */
@customElement('cv-attach-chip')
export class CvAttachChip extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: inline-flex;
                align-items: center;
                gap: 6px;
                /* One step lighter than the surface it sits on (the composer's field and the
                 * user bubble share one): each level nested in another goes lighter, never back
                 * down, or the chip reads as a hole in the bubble. The outline is what makes it
                 * an object rather than a patch. The link colour because every chip opens
                 * something: an image, a file, a place in the editor. */
                background: var(--colorNeutralBackground1Hover);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                padding: 3px 8px 3px 6px;
                font-size: var(--fontSizeBase200);
                color: var(--colorBrandForegroundLink);
                max-width: 280px;
                cursor: pointer;
            }
            :host(:hover) {
                border-color: var(--colorNeutralStrokeAccessible);
            }
            /* The ✕ brings its own room on the right. */
            :host([removable]) {
                padding-right: 4px;
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
            /* Remove ✕: inside the chip, at its end. It shows on hover, in room that is always
             * kept for it: a chip that widened under the pointer would push the ones beside it. */
            .remove {
                display: inline-flex;
                align-items: center;
                justify-content: center;
                flex-shrink: 0;
                width: 16px;
                height: 16px;
                padding: 0;
                border: none;
                background: transparent;
                color: var(--colorNeutralForeground3);
                cursor: pointer;
                line-height: 1;
                opacity: 0;
                transition: opacity 0.15s;
            }
            :host(:hover) .remove,
            :host(:focus-within) .remove {
                opacity: 1;
            }
            .remove svg {
                width: 12px;
                height: 12px;
            }
            .remove:hover {
                color: var(--colorNeutralForeground1);
            }
        `,
    ];

    @property() src = '';
    @property() label = '';
    @property({ type: Boolean, reflect: true }) removable = false;

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
                    ? html`<button
                          class="remove"
                          data-tip="Remove"
                          aria-label="Remove"
                          @click=${this._remove}
                      >
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

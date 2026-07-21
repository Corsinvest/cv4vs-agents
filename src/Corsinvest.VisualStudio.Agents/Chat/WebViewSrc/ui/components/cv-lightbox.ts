/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss20Regular from '@fluentui/svg-icons/icons/dismiss_20_regular.svg';
import './cv-copy-btn';
import { iconStyles } from '../styles/shared';
import type { LightboxRequest } from '../../core/types';
import { CvDialogBase } from './cv-dialog-base';

/**
 * Full-screen image viewer. Created on demand by dialog-host (`openLightbox()`),
 * which owns focus + the Esc stack; this component just renders and emits `close`.
 * Shadow DOM + static styles. Built on Fluent v3 `<fluent-dialog>` so ESC dismiss,
 * backdrop click and focus trap are handled by Fluent natively.
 */
@customElement('cv-lightbox')
export class CvLightbox extends CvDialogBase {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* Strip Fluent's default body padding/title-bar — go straight into our own
             * frame. Cap to the viewport and clip so the image never scrolls. */
            fluent-dialog::part(dialog) {
                padding: 0;
                max-width: 92vw;
                max-height: 92vh;
                overflow: hidden;
            }
            .frame {
                position: relative;
                display: block;
                line-height: 0;
                overflow: hidden;
                max-width: 92vw;
                max-height: 92vh;
            }
            /* Image carries its intrinsic size, capped slightly below the dialog cap. */
            .img {
                display: block;
                max-width: 90vw;
                max-height: 90vh;
                width: auto;
                height: auto;
                object-fit: contain;
            }
            /* Action chips (copy, close) overlaid top-right; reveal on deliberate hover. */
            .actions {
                position: absolute;
                top: 8px;
                right: 8px;
                display: flex;
                gap: 4px;
                background: var(--colorBackgroundOverlay);
                border-radius: var(--borderRadiusMedium);
                padding: 2px;
                opacity: 0;
                transition: opacity 0.15s;
            }
            .frame.armed:hover .actions,
            .actions:focus-within {
                opacity: 1;
            }
            .actions fluent-button::part(control) {
                color: var(--colorNeutralForegroundOnBrand);
            }
            .actions:hover {
                background: rgba(0, 0, 0, 0.75);
            }
        `,
    ];

    @property({ attribute: false }) req: LightboxRequest | null = null;
    // The mouse is already over the image on open (the click that opened it),
    // so :hover would show the actions immediately. Stay "disarmed" until the
    // first real mousemove, so the actions only appear on a deliberate hover.
    @state() private _armed = false;

    override updated(changed: Map<string, unknown>): void {
        super.updated(changed);
        // Disarm the hover-actions on open: the mouse is already over the image, so wait for a
        // deliberate mousemove before showing them.
        if (changed.has('open') && this.open) {
            this._armed = false;
        }
    }

    private _arm = (): void => {
        if (!this._armed) {
            this._armed = true;
        }
    };

    /** Backdrop dismiss: our `.cv-lightbox-frame` fills the dialog and absorbs
     *  Fluent's own backdrop clicks, so re-check at the frame level. */
    private _onFrameClick = (e: MouseEvent): void => {
        if (e.target === e.currentTarget) {
            this._close();
        }
    };

    override render() {
        const r = this.req;
        if (!this.open || !r) {
            return nothing;
        }
        const src = r.src;
        const fetchBlob = (): Promise<Blob> => fetch(src).then((res) => res.blob());
        return html`
            <fluent-dialog type="modal" aria-label="Image preview" @toggle=${this._onDialogToggle}>
                <div
                    class="frame ${this._armed ? 'armed' : ''}"
                    @click=${this._onFrameClick}
                    @mousemove=${this._arm}
                >
                    <img class="img" src=${src} alt=${r.name ?? ''} />
                    <div class="actions">
                        <cv-copy-btn .getBlob=${fetchBlob} title="Copy image"></cv-copy-btn>
                        <fluent-button
                            appearance="transparent"
                            icon-only
                            aria-label="Close"
                            @click=${() => this._close()}
                        >
                            ${unsafeHTML(Dismiss20Regular)}
                        </fluent-button>
                    </div>
                </div>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-lightbox': CvLightbox;
    }
}

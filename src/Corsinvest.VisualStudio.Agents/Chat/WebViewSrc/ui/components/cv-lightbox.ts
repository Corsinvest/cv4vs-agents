/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
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
            /* Cap the dialog to the viewport; the standard header sits above the image. */
            fluent-dialog::part(dialog) {
                max-width: 92vw;
                max-height: 92vh;
                overflow: hidden;
            }
            .title {
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .header-actions {
                display: flex;
                align-items: center;
                gap: 4px;
            }
            .frame {
                display: block;
                line-height: 0;
                overflow: hidden;
                max-width: 88vw;
                max-height: 84vh;
            }
            /* Image carries its intrinsic size, capped below the dialog cap (header takes some height). */
            .img {
                display: block;
                max-width: 88vw;
                max-height: 84vh;
                width: auto;
                height: auto;
                object-fit: contain;
            }
        `,
    ];

    @property({ attribute: false }) req: LightboxRequest | null = null;

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
        const name = r.name || 'Image';
        // The clipboard only accepts image/png for a ClipboardItem — a jpeg/webp write is rejected
        // (silent catch = no checkmark). Draw the image onto a canvas and re-encode as PNG so any
        // source format copies.
        const fetchBlob = (): Promise<Blob> =>
            new Promise((resolve, reject) => {
                const img = new Image();
                img.crossOrigin = 'anonymous';
                img.onload = () => {
                    const canvas = document.createElement('canvas');
                    canvas.width = img.naturalWidth;
                    canvas.height = img.naturalHeight;
                    const ctx = canvas.getContext('2d');
                    if (!ctx) {
                        reject(new Error('no 2d context'));
                        return;
                    }
                    ctx.drawImage(img, 0, 0);
                    canvas.toBlob(
                        (blob) => (blob ? resolve(blob) : reject(new Error('toBlob failed'))),
                        'image/png',
                    );
                };
                img.onerror = () => reject(new Error('image load failed'));
                img.src = src;
            });
        // Standard dialog chrome (like the diff dialog): title + copy in the header, the ✕ in the
        // close slot — all ABOVE the image, never overlaid on it.
        return html`
            <fluent-dialog type="modal" aria-label="Image preview" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <span slot="title" class="title" title=${name}>${name}</span>

                    <!-- Copy + close together on the right (title-action slot), copy first. -->
                    <span slot="title-action" class="header-actions">
                        <cv-copy-btn .getBlob=${fetchBlob} title="Copy image"></cv-copy-btn>
                        <fluent-button
                            appearance="transparent"
                            icon-only
                            aria-label="Close"
                            @click=${() => this._close()}
                        >
                            ${unsafeHTML(Dismiss16Regular)}
                        </fluent-button>
                    </span>

                    <div class="frame" @click=${this._onFrameClick}>
                        <img class="img" src=${src} alt=${r.name ?? ''} />
                    </div>
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-lightbox': CvLightbox;
    }
}

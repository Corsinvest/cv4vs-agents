/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { REMOTE_CONTROL_ICON } from '../../core/commands/builtin-commands';
import { qrSvg } from '../helpers/qr';
import { iconStyles } from '../styles/shared';

/**
 * The Remote Control session link, posted into the transcript when the bridge comes up — the
 * same thing the CLI does. In the conversation rather than a banner: the link matters for a few
 * seconds, and a row pinned above the chat would spend a line of a narrow tool window on it
 * forever.
 *
 * The QR is the point. On the desktop the link is redundant — the session is already on screen;
 * what is missing is a way to reach it from the phone, and scanning beats copying a URL.
 */
@customElement('cv-remote-card')
export class CvRemoteCard extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: block;
            }
            .card {
                display: flex;
                align-items: center;
                gap: 12px;
                padding: 10px 12px;
                border: 1px solid var(--colorNeutralStroke2);
                border-radius: var(--borderRadiusMedium);
                background: var(--colorNeutralBackground2);
            }
            /* The QR paints its own white quiet zone, so it needs a light frame of its own not to
               sit as a bright square on the dark theme. */
            .qr {
                flex-shrink: 0;
                width: 96px;
                height: 96px;
                padding: 4px;
                background: #fff;
                border-radius: var(--borderRadiusSmall);
            }
            .qr svg {
                display: block;
                width: 100%;
                height: 100%;
            }
            .body {
                min-width: 0;
                display: flex;
                flex-direction: column;
                gap: 4px;
            }
            .title {
                display: flex;
                align-items: center;
                gap: 6px;
                font-weight: var(--fontWeightSemibold);
            }
            .title svg {
                color: var(--colorBrandForeground1);
            }
            .hint {
                color: var(--colorNeutralForeground3);
                font-size: 0.9em;
            }
            /* The URL is the one part worth selecting, and it is too long for one line here. */
            .url {
                overflow-wrap: anywhere;
            }
        `,
    ];

    @property({ type: String }) accessor url = '';

    override render() {
        if (!this.url) {
            return null;
        }
        return html`
            <div class="card">
                <div class="qr">${unsafeHTML(qrSvg(this.url))}</div>
                <div class="body">
                    <div class="title">
                        ${unsafeHTML(REMOTE_CONTROL_ICON)}
                        <span>Remote Control is active</span>
                    </div>
                    <div class="hint">Scan to continue in the Claude app, or open the link.</div>
                    <fluent-link
                        class="url"
                        href=${this.url}
                        target="_blank"
                        rel="noopener noreferrer"
                        >${this.url}</fluent-link
                    >
                    <div class="hint">It ends when this session restarts.</div>
                </div>
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-remote-card': CvRemoteCard;
    }
}

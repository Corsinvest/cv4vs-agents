/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Mic16Regular from '@fluentui/svg-icons/icons/mic_16_regular.svg';
import { iconStyles } from '../styles/shared';

/**
 * Mic button using Web Speech API. Fires a `transcript` CustomEvent with
 * `{ detail: string }` whenever text is recognized (interim or final).
 * Hidden when SpeechRecognition is unavailable in the current browser.
 * Shadow DOM + static styles (Lit standard); iconStyles fills the inline SVG.
 */
@customElement('cv-mic-button')
export class CvMicButton extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* Shrink the inline icon to 16px (Fluent defaults to 20px, too big here). */
            svg {
                width: 16px;
                height: 16px;
            }
            /* While recording: red button that pulses, reads as "stop". */
            fluent-button.is-recording {
                background: var(--colorPaletteRedBackground3);
                border-color: var(--colorPaletteRedBackground3);
                color: var(--colorNeutralForegroundOnBrand);
                animation: mic-pulse 1.8s ease-in-out infinite;
            }
            @keyframes mic-pulse {
                0%,
                40% {
                    opacity: 1;
                }
                70% {
                    opacity: 0.25;
                }
                100% {
                    opacity: 1;
                }
            }
        `,
    ];

    @state() private _recording = false;

    private _speech: any = null;
    private _hasSpeech =
        typeof (window as any).SpeechRecognition !== 'undefined' ||
        typeof (window as any).webkitSpeechRecognition !== 'undefined';

    // Text accumulated from final results in this recording session.
    private _finalText = '';

    private _onClick = (): void => {
        if (this._recording) {
            this._speech?.stop();
            return;
        }
        const SR = (window as any).SpeechRecognition ?? (window as any).webkitSpeechRecognition;
        if (!SR) {
            return;
        }
        const sr = new SR();
        sr.lang = navigator.language || 'en-US';
        sr.interimResults = true;
        sr.continuous = true;
        this._finalText = '';
        this.dispatchEvent(new CustomEvent('recording-start', { bubbles: true, composed: true }));

        sr.onresult = (e: any) => {
            let interim = '';
            for (let i = e.resultIndex; i < e.results.length; i++) {
                const t = e.results[i][0].transcript;
                if (e.results[i].isFinal) {
                    this._finalText += t;
                } else {
                    interim += t;
                }
            }
            this.dispatchEvent(
                new CustomEvent('transcript', {
                    detail: { text: this._finalText + interim, isFinal: interim === '' },
                    bubbles: true,
                    composed: true,
                }),
            );
        };
        sr.onend = () => {
            this._recording = false;
            this._speech = null;
            this.dispatchEvent(new CustomEvent('recording-end', { bubbles: true, composed: true }));
        };
        sr.onerror = () => {
            this._recording = false;
            this._speech = null;
        };
        this._speech = sr;
        this._recording = true;
        sr.start();
    };

    override render() {
        if (!this._hasSpeech) {
            return nothing;
        }
        return html`<fluent-button
            id="btn-mic"
            appearance="subtle"
            icon-only
            size="small"
            title=${this._recording ? 'Stop recording' : 'Voice dictation'}
            class=${this._recording ? 'is-recording' : ''}
            @click=${this._onClick}
            >${unsafeHTML(Mic16Regular)}</fluent-button
        >`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-mic-button': CvMicButton;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Mic16Regular from '@fluentui/svg-icons/icons/mic_16_regular.svg';
import { iconStyles, iconTriggerStyles, tooltipStyles } from '../styles/shared';

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
        iconTriggerStyles,
        tooltipStyles,
        css`
            /* While recording: red button that pulses, reads as "stop". The colour is set on the
               host, over the subtle appearance — this state is the one thing Fluent has no
               variant for. */
            .trigger.is-recording,
            .trigger.is-recording:hover {
                background: var(--colorPaletteRedBackground3);
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

    /**
     * Stop the recognition session when the button goes away (pane closed mid-dictation): the
     * engine holds the microphone until someone says otherwise, and nothing else would.
     *
     * `abort()` and not `stop()`: stop() finalises the pending utterance and fires one last
     * `onresult`, which would dispatch `transcript` at a detached listener. The handlers are
     * cleared first because abort() still fires `onend`, and that branch dispatches
     * `recording-end` — cv-prompt's handler reads its textarea, which is gone by then.
     */
    override disconnectedCallback(): void {
        super.disconnectedCallback();
        const sr = this._speech;
        if (!sr) {
            return;
        }
        this._speech = null;
        this._recording = false;
        sr.onresult = null;
        sr.onend = null;
        sr.onerror = null;
        sr.abort();
    }

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
                class=${`trigger${this._recording ? ' is-recording' : ''}`}
                appearance="subtle"
                shape="rounded"
                size="small"
                icon-only
                @click=${this._onClick}
            >
                ${unsafeHTML(Mic16Regular)}
            </fluent-button>
            <!-- positioning=after, like the file chip and the remote chip further along this row:
                 the button sits at the left end of the toolbar, so the tooltip opens towards the
                 space there is, and above would land on the field. -->
            <fluent-tooltip anchor="btn-mic" positioning="after"
                >${this._recording ? 'Stop recording' : 'Voice recording'}</fluent-tooltip
            >`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-mic-button': CvMicButton;
    }
}

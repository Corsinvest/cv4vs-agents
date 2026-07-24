/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Speaker216Regular from '@fluentui/svg-icons/icons/speaker_2_16_regular.svg';
import Pause16Regular from '@fluentui/svg-icons/icons/pause_16_regular.svg';
import { iconStyles } from '../styles/shared';
import { renderMarkdown } from '../../core/markdown';

// Web Speech synthesis is a single global engine — only one utterance plays at a time. Track which
// button owns the current speech so a new click can stop the previous one and every button reflects
// the real state (a click elsewhere, or speech ending, flips the icon back). speechSynthesis itself
// is the source of truth; this set just lets active buttons re-render.
const active = new Set<CvSpeakBtn>();
function stopAll(except?: CvSpeakBtn): void {
    for (const b of active) {
        if (b !== except) {
            b._stopSpeaking();
        }
    }
}

/** Flatten markdown to speakable plain text — otherwise the engine reads "**", "##", backticks and
 *  link URLs aloud. Reuses the real markdown parser (marked) → HTML → textContent, so tables, nested
 *  lists, links (label kept, URL dropped) etc. all come out right. Code blocks are removed first: you
 *  don't want a fenced snippet recited character by character. */
function toSpeech(md: string): string {
    if (!md) {
        return '';
    }
    const div = document.createElement('div');
    div.innerHTML = renderMarkdown(md);
    div.querySelectorAll('pre').forEach((el) => el.remove());
    return (div.textContent ?? '').replace(/\n{3,}/g, '\n\n').trim();
}

/**
 * Read-aloud icon button (Web Speech API `speechSynthesis`, native in WebView2, zero deps). Click the
 * speaker to read the `text`; while reading the icon is a pause — click it to pause, click again to
 * resume from where it left off. Starting another button stops this one. Hidden when the API is
 * unavailable. Shadow DOM + static styles; host is display:contents so it sits in the parent flex.
 */
@customElement('cv-speak-btn')
export class CvSpeakBtn extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            .icon-btn {
                display: inline-flex;
                align-items: center;
                justify-content: center;
                padding: 3px;
                border: 0;
                border-radius: var(--borderRadiusSmall);
                background: transparent;
                color: inherit;
                cursor: pointer;
                opacity: 0.75;
            }
            .icon-btn:hover {
                background: var(--colorSubtleBackgroundHover, rgba(127, 127, 127, 0.12));
                opacity: 1;
            }
            .icon-btn:focus-visible {
                outline: 1px solid var(--colorStrokeFocus2, currentColor);
                outline-offset: 1px;
            }
            .icon-btn svg {
                width: 14px;
                height: 14px;
                display: block;
            }
            /* Speaking: a tinted pause icon — click to pause (the speaker icon returns; click resumes). */
            .icon-btn.speaking svg {
                color: var(--colorBrandForeground1);
                fill: var(--colorBrandForeground1);
            }
        `,
    ];

    @property() text = '';
    @property() override title = 'Read aloud';

    // idle = not reading (speaker icon) · speaking = reading (pause icon) · paused = held mid-read
    // (speaker icon — click resumes from where it left off).
    @state() private _state: 'idle' | 'speaking' | 'paused' = 'idle';

    private static readonly _supported =
        typeof window !== 'undefined' && 'speechSynthesis' in window;

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._stopSpeaking();
        active.delete(this);
    }

    /** Fully stop this button's speech and reset to idle. Used when another button starts, or on
     *  unmount. (cancel() clears the queue — only one utterance is ever queued.) */
    _stopSpeaking(): void {
        if (this._state !== 'idle') {
            this._state = 'idle';
            window.speechSynthesis.cancel();
        }
    }

    private _onClick = (e: Event): void => {
        e.stopPropagation();
        if (this._state === 'speaking') {
            window.speechSynthesis.pause();
            this._state = 'paused';
            return;
        }
        if (this._state === 'paused') {
            window.speechSynthesis.resume();
            this._state = 'speaking';
            return;
        }
        // idle → start reading
        const text = toSpeech(this.text);
        if (!text) {
            return;
        }
        stopAll(this); // one engine — stop whatever else was reading
        window.speechSynthesis.cancel();
        const u = new SpeechSynthesisUtterance(text);
        // Reset to idle when the engine finishes or errors so the icon never stays stuck on "pause".
        u.onend = () => {
            this._state = 'idle';
        };
        u.onerror = () => {
            this._state = 'idle';
        };
        this._state = 'speaking';
        active.add(this);
        window.speechSynthesis.speak(u);
    };

    override render() {
        if (!CvSpeakBtn._supported) {
            return nothing;
        }
        const speaking = this._state === 'speaking';
        return html`
            <button
                part="button"
                class="icon-btn ${speaking ? 'speaking' : ''}"
                title=${speaking ? 'Pause' : this._state === 'paused' ? 'Resume' : this.title}
                @click=${this._onClick}
            >
                ${unsafeHTML(speaking ? Pause16Regular : Speaker216Regular)}
            </button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-speak-btn': CvSpeakBtn;
    }
}

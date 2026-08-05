/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { formatAbsolute, formatTimeAgo } from '../helpers/format';

/**
 * The "x ago" stamp under a message, recomputed when the pointer arrives.
 *
 * Elapsed time is not a property, so nothing re-renders as it passes: a stamp drawn when the
 * message arrived reads "1 second ago" for as long as the message stays on screen. The row is
 * always in the DOM and only revealed by `opacity` on hover, so the hover is both the moment the
 * stamp becomes legible and the only moment worth recomputing it.
 *
 * This exists as an element, rather than the plain template it used to be, for one reason: the
 * hover must change rendered text, and the only safe way to do that is to let Lit render it again.
 * The previous version wrote `textContent` onto the span holding the `${...}` binding, which
 * destroyed that ChildPart's markers — after which every later render of the whole chat threw
 * `Cannot set properties of null (setting 'data')` and the pane stopped updating entirely.
 *
 * Light DOM: `.cv-ts` is styled by the chat's global stylesheet, and the hover-reveal `opacity`
 * lives on the parent row.
 */
@customElement('cv-time-ago')
export class CvTimeAgo extends LitElement {
    /** Epoch ms of the message. 0 renders nothing. */
    @property({ type: Number }) ms = 0;

    /** The clock the stamp was last computed against. Bumping it is what re-renders the text. */
    @state() private _now = Date.now();

    override createRenderRoot() {
        return this;
    }

    override render() {
        if (!this.ms) {
            return html``;
        }
        return html`<span
            class="cv-ts"
            title=${formatAbsolute(this.ms)}
            @mouseenter=${() => {
                this._now = Date.now();
            }}
            >${formatTimeAgo(this.ms, this._now)}</span
        >`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-time-ago': CvTimeAgo;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { formatDuration } from '../helpers/format';

/**
 * The running clock on an Agent row, advancing once a second while the sub-agent works.
 *
 * It has to tick on its own: the sub-agent's own figures only move when it reports a tool use, and
 * those can be ten seconds apart, so a badge fed from them sits still long enough to read as stuck.
 *
 * This exists as an element for the same reason `cv-time-ago` does — the tick must change rendered
 * text, and the only safe way to do that is to let Lit render it again. Writing `textContent` onto
 * a span that holds a `${...}` binding destroys that ChildPart's markers, after which every later
 * render of the whole chat throws `Cannot set properties of null (setting 'data')` and the pane
 * stops updating. The row used to repaint the badge that way, guarded only by the fact that the
 * corrupted node was replaced when the Agent finished.
 *
 * Owning the timer here is also what makes re-rendering cheap enough to be an option at all:
 * `requestUpdate` on the tool row would rebuild the whole row — body, IN/OUT, highlighted code,
 * nested children — sixty times a minute for a number. Here it re-renders one span.
 *
 * Light DOM: the badge is styled by the chat's global stylesheet, which cannot reach into a shadow
 * root.
 */
@customElement('cv-elapsed')
export class CvElapsed extends LitElement {
    /** Epoch ms the sub-agent started. 0 renders nothing and keeps the timer off.
     *
     *  There is deliberately no fallback to the task's own `usage.durationMs`: that is the total the
     *  sub-agent REPORTS, which only moves when it reports a tool use, and rendering it here would
     *  put a stalled number where a running clock is expected — the very thing this badge exists to
     *  avoid. Elapsed is derived from startedAt or it is not shown. */
    @property({ type: Number }) startedAt = 0;

    /** The clock the badge was last drawn against. Bumping it is what re-renders the text. */
    @state() private _now = Date.now();

    private _timer = 0;

    override createRenderRoot() {
        return this;
    }

    override connectedCallback() {
        super.connectedCallback();
        this._sync();
    }

    override disconnectedCallback() {
        super.disconnectedCallback();
        this._stop();
    }

    override updated() {
        // startedAt arrives (or is cleared) by property, so the timer follows it rather than the
        // element's lifetime: a row can be built before its task is known.
        this._sync();
    }

    private _sync(): void {
        if (this.startedAt > 0 && !this._timer) {
            this._timer = window.setInterval(() => {
                this._now = Date.now();
            }, 1000);
        } else if (this.startedAt <= 0 && this._timer) {
            this._stop();
        }
    }

    private _stop(): void {
        clearInterval(this._timer);
        this._timer = 0;
    }

    override render() {
        if (this.startedAt <= 0) {
            return html``;
        }
        return html`<span class="cv-agent-time"
            >${formatDuration(this._now - this.startedAt)}</span
        >`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-elapsed': CvElapsed;
    }
}

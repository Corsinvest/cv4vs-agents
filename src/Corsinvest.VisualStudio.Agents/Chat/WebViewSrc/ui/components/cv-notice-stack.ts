/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Info16Regular from '@fluentui/svg-icons/icons/info_16_regular.svg';
import Warning16Filled from '@fluentui/svg-icons/icons/warning_16_filled.svg';
import CheckmarkCircle16Filled from '@fluentui/svg-icons/icons/checkmark_circle_16_filled.svg';
import DismissCircle16Filled from '@fluentui/svg-icons/icons/dismiss_circle_16_filled.svg';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { bridge } from '../../core/bridge';
import type { Notice, NoticeDismissedDetail } from '../../core/types';

// Same glyph set Fluent's own message-bar uses: info outlined, the actionable severities filled so
// they read at a glance.
const ICONS: Record<Notice['severity'], string> = {
    info: Info16Regular,
    success: CheckmarkCircle16Filled,
    warning: Warning16Filled,
    error: DismissCircle16Filled,
};

/**
 * A stack of dismissible notices (fluent-message-bar), newest last, that OWNS its queue: callers just
 * `push()` and forget — dedup by key, the FIFO cap, the auto-dismiss timers and the ✕ are all handled
 * here. `dismissByKey` is for a condition that clears upstream (a rate limit lifting).
 *
 * Each instance is an independent queue, so the same component hosts two separate places: the
 * system-level strip at the top of the chat and the per-turn one above the composer.
 */
@customElement('cv-notice-stack')
export class CvNoticeStack extends LitElement {
    static override styles = css`
        :host {
            display: block;
        }
        .stack {
            display: flex;
            flex-direction: column;
            gap: 4px;
        }
        /* Only layout on the fluent element (fluent-pure rule) — no colours/borders. */
        fluent-message-bar {
            width: 100%;
        }
        /* Single-line layout keeps the action button in row (compact), but the CLI's advisories can be
         * a full sentence — let the text wrap instead of being clipped. */
        .msg {
            white-space: normal;
            overflow-wrap: anywhere;
        }
        .ico {
            display: inline-flex;
            align-items: center;
        }
        /* The Fluent svg paths carry no fill, so they'd paint black — take the span's colour, which
         * the severity rules below set (the same approach Fluent's own message-bar demo uses). */
        .ico svg {
            width: 16px;
            height: 16px;
            display: block;
            fill: currentColor;
        }
        /* Semantic tokens, so both themes stay legible. */
        .ico.info {
            color: var(--colorNeutralForeground2);
        }
        .ico.success {
            color: var(--colorPaletteGreenForeground1);
        }
        .ico.warning {
            color: var(--colorPaletteDarkOrangeForeground1);
        }
        .ico.error {
            color: var(--colorPaletteRedForeground1);
        }
    `;

    /** How many rows are kept — the oldest falls off when a new one arrives beyond this. */
    @property({ type: Number }) max = 3;
    /** Auto-dismiss delay. Applies to whatever the notice doesn't pin with `sticky`. */
    @property({ type: Number }) autoDismissMs = 7000;

    @state() private _queue: Notice[] = [];

    private _seq = 0;
    private _timers = new Map<string, ReturnType<typeof setTimeout>>();

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this.clear();
    }

    /** Queue a notice. A repeat of the same `key` replaces its row instead of stacking. `sticky`
     *  decides whether it stays: a caller that knows (the CLI's own `level`, a condition the host
     *  clears later) sets it explicitly, otherwise warning/error default to staying and info/success
     *  to fading after `autoDismissMs`. */
    push(n: Omit<Notice, 'id'>): void {
        if (!n?.message) {
            return;
        }
        const id = `n${++this._seq}`;
        const next = n.key ? this._queue.filter((q) => q.key !== n.key) : [...this._queue];
        next.push({ ...n, id });
        while (next.length > this.max) {
            const dropped = next.shift();
            if (dropped) {
                this._clearTimer(dropped.id);
            }
        }
        this._queue = next;
        const stays = n.sticky ?? (n.severity === 'warning' || n.severity === 'error');
        if (!stays) {
            this._timers.set(
                id,
                setTimeout(() => {
                    this._timers.delete(id);
                    this._remove(id);
                }, this.autoDismissMs),
            );
        }
    }

    /** Drop the row for a condition that cleared upstream (e.g. the rate limit lifted). */
    dismissByKey(key: string): void {
        const row = this._queue.find((q) => q.key === key);
        if (row) {
            this._remove(row.id);
        }
    }

    /** Drop every row except the given keys — for a scope change (a new session) that invalidates
     *  most rows but not the ones tracking something outside it (a dead CLI process). */
    dismissExcept(...keepKeys: string[]): void {
        for (const row of this._queue) {
            if (!row.key || !keepKeys.includes(row.key)) {
                this._clearTimer(row.id);
            }
        }
        this._queue = this._queue.filter((q) => q.key && keepKeys.includes(q.key));
    }

    /** Drop every row and stop pending timers (conversation cleared, unmount). */
    clear(): void {
        for (const t of this._timers.values()) {
            clearTimeout(t);
        }
        this._timers.clear();
        this._queue = [];
    }

    /** The action button sends its bridge message to the host (e.g. open the Output window) and
     *  leaves the row up — the host clears it when the condition resolves. */
    private _runAction(n: Notice): void {
        if (n.actionMessage) {
            bridge.sendNotification(n.actionMessage, {});
        }
    }

    /** The ✕. Kept apart from `_remove`, which also serves the auto-dismiss timer and `dismissByKey`
     *  — neither of those is the user deciding they have read the thing. Only this one is worth
     *  telling the caller about: one that re-pushes the same key on a schedule it doesn't control
     *  (the CLI re-sends `rate_limit_info` every turn) has no other way to know to stop. */
    private _dismissByUser(n: Notice): void {
        this.dispatchEvent(
            new CustomEvent<NoticeDismissedDetail>('notice-dismissed', {
                detail: { key: n.key },
                bubbles: true,
                composed: true,
            }),
        );
        this._remove(n.id);
    }

    private _remove(id: string): void {
        this._clearTimer(id);
        this._queue = this._queue.filter((q) => q.id !== id);
    }

    private _clearTimer(id: string): void {
        const t = this._timers.get(id);
        if (t) {
            clearTimeout(t);
            this._timers.delete(id);
        }
    }

    override render() {
        if (this._queue.length === 0) {
            return nothing;
        }
        return html`
            <div class="stack">
                ${this._queue.map(
                    (n) => html`
                        <!-- Default (single-line) layout: the text and the action button stay on one
                             row. multiline would push the action onto its own line and nearly double
                             the height — too much in a narrow tool window. -->
                        <fluent-message-bar intent=${n.severity}>
                            <span slot="icon" class="ico ${n.severity}"
                                >${unsafeHTML(ICONS[n.severity])}</span
                            >
                            <span class="msg">${unsafeHTML(n.message)}</span>
                            ${
                                n.actionLabel && n.actionMessage
                                    ? html`<fluent-button
                                          slot="actions"
                                          appearance="transparent"
                                          size="small"
                                          @click=${() => this._runAction(n)}
                                          >${n.actionLabel}</fluent-button
                                      >`
                                    : nothing
                            }
                            <fluent-button
                                slot="dismiss"
                                appearance="transparent"
                                icon-only
                                title="Dismiss"
                                @click=${() => this._dismissByUser(n)}
                            >
                                ${unsafeHTML(Dismiss16Regular)}
                            </fluent-button>
                        </fluent-message-bar>
                    `,
                )}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-notice-stack': CvNoticeStack;
    }
}

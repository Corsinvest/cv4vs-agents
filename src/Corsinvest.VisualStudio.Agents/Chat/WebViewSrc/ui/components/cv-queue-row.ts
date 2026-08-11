/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Delete16Regular from '@fluentui/svg-icons/icons/delete_16_regular.svg';
import TextBulletList16Regular from '@fluentui/svg-icons/icons/text_bullet_list_16_regular.svg';
import { parseIdeContextTags } from '../../core/ide';
import { iconStyles } from '../styles/shared';

export interface QueuedMessage {
    text: string;
    uuid: string;
}

/** The row above the composer while messages are waiting to be sent. Stop drops the whole queue
 *  but stops the running turn with it, which is not what you want when it is one message you
 *  regret — so the bin here empties the queue on its own, and the list takes them out one at a
 *  time.
 *
 *  The row renders nothing when the queue is empty, so it costs no space the rest of the time —
 *  the toolbar below is already full. With a single message its text sits in the row, and the bin
 *  has a visible target; past that the text no longer fits, so a count opens the list instead. The
 *  bin keeps its place through both, which is what lets you hit it without looking.
 *
 *  Shadow DOM + static styles, and the popover shares this root so it can be positioned against
 *  the trigger — same reason as cv-subagent-chip, which this follows throughout. */
@customElement('cv-queue-row')
export class CvQueueRow extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: block;
                position: relative;
            }
            .row {
                display: flex;
                align-items: center;
                gap: 4px;
                padding: 3px 4px 3px 8px;
                border-bottom: 1px solid var(--colorNeutralStroke3);
            }
            .label {
                flex-shrink: 0;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
            }
            /* The single-message text: one line, ellipsised. The bubble above still holds the
               whole thing, so this only has to be enough to recognise which one it is. */
            .text {
                flex: 1;
                min-width: 0;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                font-size: 0.85em;
                color: var(--colorNeutralForeground3);
            }
            .spacer {
                flex: 1;
            }
            /* Triggers are <fluent-button> — keep them pure (layout only). */
            .count-btn,
            .clear-btn,
            .drop-btn {
                flex-shrink: 0;
            }
            .count-btn svg,
            .clear-btn svg,
            .drop-btn svg {
                width: 16px;
                height: 16px;
                display: block;
            }
            /* Wrap the icon so the badge can overlay its corner rather than sit in the flow. */
            .icon-wrap {
                position: relative;
                display: inline-flex;
            }
            .count {
                position: absolute;
                top: -7px;
                right: -9px;
            }
            /* Red on the glyph, not the button: it keeps the Fluent component pure, and grey
               would read as disabled. Same treatment as the Stop squares on the sub-agent chip. */
            .clear-btn svg,
            .drop-btn svg {
                color: var(--colorStatusDangerForeground1, #d13438);
            }

            /* As wide as the composer it sits on: a queued message is read here or nowhere, so
               narrowing it would only add wrapping to text that is already the point. */
            .popover {
                position: absolute;
                bottom: calc(100% + 4px);
                left: 0;
                right: 0;
                z-index: 1000;
                padding: 8px 10px;
                max-height: 50vh;
                overflow-y: auto;
                font-size: var(--fontSizeBase300);
                background: var(--colorNeutralBackground1);
                color: var(--colorNeutralForeground1);
                border: 1px solid var(--colorNeutralStroke1);
                border-radius: var(--borderRadiusMedium);
                box-shadow: var(--shadow16);
            }
            .popover[hidden] {
                display: none;
            }
            .head {
                font-size: 1.1em;
                font-weight: var(--fontWeightSemibold);
                margin-bottom: 10px;
            }
            /* Top-aligned: the text is several lines now, and a bin centred against it drifts
               away from the row it belongs to. */
            .item {
                display: flex;
                align-items: flex-start;
                gap: 8px;
                padding: 6px 0;
                border-bottom: 1px solid var(--colorNeutralStroke3);
            }
            .item:last-child {
                border-bottom: none;
            }
            /* The send order, which is the one thing the faded bubbles do not show at a glance. */
            .ord {
                flex-shrink: 0;
                min-width: 12px;
                font-size: 0.82em;
                color: var(--colorNeutralForeground3);
                font-variant-numeric: tabular-nums;
            }
            /* The whole message, not a label: by the time a turn has been running the echoed
               bubble has scrolled out of the viewport, so this is the only place left to read
               what is about to be sent. Clamped so one long message cannot push the others out
               of reach; the title attribute still carries the rest. */
            .item-text {
                flex: 1;
                min-width: 0;
                white-space: pre-wrap;
                overflow-wrap: anywhere;
                overflow: hidden;
                display: -webkit-box;
                -webkit-box-orient: vertical;
                -webkit-line-clamp: 6;
            }
        `,
    ];

    @property({ attribute: false }) messages: QueuedMessage[] = [];

    @state() private _open = false;

    override connectedCallback(): void {
        super.connectedCallback();
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
    }

    // Light dismiss: a click outside closes the list (composedPath crosses the shadow).
    private _onDocPointerDown = (e: PointerEvent): void => {
        if (this._open && !e.composedPath().includes(this)) {
            this._open = false;
        }
    };

    override willUpdate(): void {
        // Taking the second-to-last one out drops the row back to its single-message form, where
        // there is no trigger to close the list — so it has to close itself.
        if (this._open && this.messages.length < 2) {
            this._open = false;
        }
    }

    private _toggle = (): void => {
        this._open = !this._open;
    };

    /** What the user actually typed. The queued payload carries the `<ide_*>` block the composer
     *  prepends, which the CLI needs and a reader does not — the same strip cv-message applies
     *  before showing a user bubble. */
    private static _shown(text: string): string {
        return parseIdeContextTags(text).text;
    }

    private _drop(uuid: string): void {
        this.dispatchEvent(
            new CustomEvent('drop-queued', { detail: { uuid }, bubbles: true, composed: true }),
        );
    }

    private _clear = (): void => {
        this.dispatchEvent(new CustomEvent('clear-queue', { bubbles: true, composed: true }));
    };

    private _renderClear(count: number) {
        return html`<fluent-button
            class="clear-btn"
            appearance="transparent"
            size="small"
            title=${count === 1 ? 'Remove from queue' : 'Clear queue'}
            aria-label=${count === 1 ? 'Remove from queue' : 'Clear queue'}
            @click=${this._clear}
            >${unsafeHTML(Delete16Regular)}</fluent-button
        >`;
    }

    override render() {
        const n = this.messages.length;
        if (n === 0) {
            return nothing;
        }

        return html`<div class="row">
                <span class="label">Queued</span>
                ${
                    n === 1
                        ? html`<span class="text" title=${CvQueueRow._shown(this.messages[0].text)}
                              >${CvQueueRow._shown(this.messages[0].text)}</span
                          >`
                        : html`<span class="spacer"></span>
                              <fluent-button
                                  class="count-btn"
                                  appearance="subtle"
                                  size="small"
                                  icon-only
                                  aria-label=${`${n} queued messages`}
                                  aria-expanded=${this._open}
                                  @click=${this._toggle}
                              >
                                  <span class="icon-wrap">
                                      ${unsafeHTML(TextBulletList16Regular)}
                                      <fluent-counter-badge
                                          class="count"
                                          count=${n}
                                          size="small"
                                      ></fluent-counter-badge>
                                  </span>
                              </fluent-button>`
                }
                ${this._renderClear(n)}
            </div>
            <div class="popover" ?hidden=${!this._open}>
                <div class="head">Not sent yet (${n})</div>
                ${this.messages.map(
                    (m, i) =>
                        html`<div class="item">
                            <span class="ord">${i + 1}</span>
                            <span class="item-text" title=${CvQueueRow._shown(m.text)}
                                >${CvQueueRow._shown(m.text)}</span
                            >
                            <fluent-button
                                class="drop-btn"
                                appearance="transparent"
                                size="small"
                                title="Remove from queue"
                                aria-label="Remove from queue"
                                @click=${() => this._drop(m.uuid)}
                                >${unsafeHTML(Delete16Regular)}</fluent-button
                            >
                        </div>`,
                )}
            </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-queue-row': CvQueueRow;
    }
}

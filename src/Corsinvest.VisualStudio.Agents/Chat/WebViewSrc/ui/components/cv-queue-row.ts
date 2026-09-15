/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Delete16Regular from '@fluentui/svg-icons/icons/delete_16_regular.svg';
import TextBulletList16Regular from '@fluentui/svg-icons/icons/text_bullet_list_16_regular.svg';
import { cleanMessageOnlyText } from '../../core/ide';
import { iconStyles } from '../styles/shared';
import { iconUrl } from '../../core/icon-url';
import type { Attachment } from '../../core/types';
import './cv-attach-chip';

export interface QueuedMessage {
    text: string;
    uuid: string;
    /** What the message carries. cv-prompt's queue entries have always held these; the type
     *  simply did not say so, and the list could not tell a prompt with a screenshot from one
     *  without. */
    attachments?: Attachment[];
}

/** The row above the composer while messages are waiting to be sent. Stop drops the whole queue
 *  but stops the running turn with it, which is not what you want when it is one message you
 *  regret — so the bin here empties the queue on its own, and the list takes them out one at a
 *  time.
 *
 *  The row renders nothing when the queue is empty, so it costs no space the rest of the time —
 *  the toolbar below is already full. It is a count at any length: a single message used to have
 *  its text inline instead, truncated to whatever the label and the buttons left over, where it
 *  could be neither read in full nor copied. The list is the one place the messages are shown.
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
            .spacer {
                flex: 1;
            }
            /* Triggers are <fluent-button> — keep them pure (layout only). */
            .count-btn,
            .clear-btn,
            .drop-btn {
                flex-shrink: 0;
            }
            /* Cut to icon width, like every other icon trigger in the composer: at Fluent's width
               for a labelled button the bin never reached the edge the send button below it sits
               on, however much padding the row was given. */
            .count-btn,
            .clear-btn {
                padding: 3px;
                min-width: 0;
            }
            /* The row's 4px gap was set when these were wide enough to space themselves; at icon
               width it leaves the bin looking like part of the count control. */
            .count-btn {
                margin-right: 4px;
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
            /* Enough of the message to tell it from the others, not the message itself: by the
               time a turn has been running the echoed bubble has scrolled out of the viewport, so
               this is the only place left to check what is about to be sent — but three lines of
               a pasted function already say which one it is, and six let one entry crowd out the
               rest. The title attribute carries the whole text.
               Plain text, never rendered markdown: this is for recognising a message, and a code
               block with its own background and padding would add height exactly where there is
               none to spare. */
            .item-text {
                white-space: pre-wrap;
                overflow-wrap: anywhere;
                overflow: hidden;
                display: -webkit-box;
                -webkit-box-orient: vertical;
                -webkit-line-clamp: 3;
            }
            /* The same chip the composer and the sent bubble use, so an attachment looks the same
               at all three points of one message's life. Wraps under the text rather than beside
               it: the row is already tight between the ordinal and the bin. */
            .item-files {
                display: flex;
                flex-wrap: wrap;
                gap: 4px;
                margin-top: 4px;
            }
            .item-body {
                flex: 1;
                min-width: 0;
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
        // An empty queue renders nothing at all, trigger included, so the list has to close itself
        // — there would be no way left to dismiss it.
        if (this._open && this.messages.length === 0) {
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
        return cleanMessageOnlyText(text);
    }

    /** Built here rather than inline in the list: `.item-text` is `white-space: pre-wrap`, so the
     *  indentation the template would put around the interpolation becomes leading whitespace on
     *  screen — which is what pushed every entry away from its ordinal. */
    private static _renderItemText(text: string) {
        const shown = CvQueueRow._shown(text);
        return html`<div class="item-text" title=${shown}>${shown}</div>`;
    }

    /** What the message carries, as the chips the composer and the sent bubble already use.
     *  Not removable and not clickable: taking one attachment out of a queued message is not a
     *  thing the queue can do — the bin drops the message whole — and opening it would put a
     *  lightbox over the list you are reading. */
    private static _renderFiles(files?: Attachment[]) {
        if (!files?.length) {
            return nothing;
        }
        return html`<div class="item-files">
            ${files.map(
                (a) =>
                    html`<cv-attach-chip
                        .src=${a.isImage ? (a.preview ?? a.dataUrl) : iconUrl(a.name)}
                        .label=${a.name}
                        title=${a.name}
                    ></cv-attach-chip>`,
            )}
        </div>`;
    }

    private _drop(uuid: string): void {
        this.dispatchEvent(
            new CustomEvent('drop-queued', { detail: { uuid }, bubbles: true, composed: true }),
        );
    }

    private _clear = (): void => {
        this.dispatchEvent(new CustomEvent('clear-queue', { bubbles: true, composed: true }));
    };

    private _renderClear() {
        return html`<fluent-button
            class="clear-btn"
            appearance="subtle"
            size="small"
            icon-only
            title="Clear queue"
            aria-label="Clear queue"
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
                <span class="spacer"></span>
                <fluent-button
                    class="count-btn"
                    appearance="subtle"
                    size="small"
                    icon-only
                    aria-label=${`${n} queued message${n === 1 ? '' : 's'}`}
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
                </fluent-button>
                ${this._renderClear()}
            </div>
            <div class="popover" ?hidden=${!this._open}>
                <div class="head">Not sent yet (${n})</div>
                ${this.messages.map(
                    (m, i) =>
                        html`<div class="item">
                            <span class="ord">${i + 1}</span>
                            <div class="item-body">
                                ${CvQueueRow._renderItemText(m.text)}
                                ${CvQueueRow._renderFiles(m.attachments)}
                            </div>
                            <fluent-button
                                class="drop-btn"
                                appearance="subtle"
                                size="small"
                                icon-only
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

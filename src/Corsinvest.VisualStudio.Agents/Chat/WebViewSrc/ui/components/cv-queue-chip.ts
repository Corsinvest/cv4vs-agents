/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Delete16Regular from '@fluentui/svg-icons/icons/delete_16_regular.svg';
import Edit16Regular from '@fluentui/svg-icons/icons/edit_16_regular.svg';
import TextBulletList16Regular from '@fluentui/svg-icons/icons/text_bullet_list_16_regular.svg';
import { cleanMessageOnlyText } from '../../core/ide';
import { iconStyles } from '../styles/shared';
import { iconUrl } from '../../core/icon-url';
import type { Attachment } from '../../core/types';
import './cv-attach-chip';
import './cv-popover-list';

export interface QueuedMessage {
    text: string;
    uuid: string;
    /** What the message carries. cv-prompt's queue entries have always held these; the type
     *  simply did not say so, and the list could not tell a prompt with a screenshot from one
     *  without. */
    attachments?: Attachment[];
    /** Entries sharing this leave as one message (Alt+Enter). They stay separate rows — each keeps
     *  its own edit and remove — so the rule down their left is what says they travel together. */
    groupId?: string;
}

/** A toolbar chip counting the messages waiting to be sent, and the list behind it. Stop drops the
 *  whole queue but stops the running turn with it, which is not what you want when it is one
 *  message you regret — so the bin in the list's head empties the queue on its own, and each item
 *  takes itself out.
 *
 *  A bin on the row and words in the head, not two bins: they sit a few pixels apart and differ
 *  only in how much they take, so with the same glyph position would be the only thing telling
 *  them apart — and getting it wrong costs the whole queue rather than one message. A cross is
 *  kept out of here entirely: the composer's editing bar uses one to CLOSE, and the same shape
 *  meaning "close" in one place and "delete" in another is the confusion this avoids. Both are
 *  red, being the way out of what they sit on.
 *
 *  Renders nothing when the queue is empty, so it costs no room the rest of the time. It was a
 *  full-width row above the composer, holding a single message's text inline — truncated to
 *  whatever the label and the buttons left over, where it could be neither read in full nor copied.
 *  With the list always answering that, the row had a label and a badge left on it, which is a chip.
 *
 *  The list itself is cv-popover-list, like the model, permission and command pickers: it owns the
 *  panel, its placement over the composer and the keyboard navigation, and takes the row content
 *  through renderRow. Row styles have to live in that component — renderRow's markup renders into
 *  its shadow — which is where .row-icon and the others already are, for the same reason. */
@customElement('cv-queue-chip')
export class CvQueueChip extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* Deliberately NOT position:relative, unlike cv-subagent-chip: the popover below is
               absolute and has to resolve against the composer (#box), not against this chip, so
               it can span the whole width the way the model and command lists do. */
            :host {
                display: inline-flex;
            }
            /* Trigger is a <fluent-button> — keep it pure (layout only). Spaced from the toolbar
               buttons either side by the same 4px cv-subagent-chip uses. */
            .chip {
                display: inline-flex;
                align-items: center;
                flex-shrink: 0;
                margin-inline-start: 4px;
                padding: 3px;
                min-width: 0;
            }
            .chip svg {
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

    /** The whole message in the title, since the row clips it to one line: the list is where you
     *  check what is about to be sent, and by then its faded bubble has usually scrolled away. */
    private static _renderItemText(text: string) {
        const shown = CvQueueChip._shown(text);
        return html`<span title=${shown}>${shown}</span>`;
    }

    /** What the message carries, as the chips the composer and the sent bubble already use.
     *  Not removable and not clickable: taking one attachment out of a queued message is not a
     *  thing the queue can do — the bin drops the message whole — and opening it would put a
     *  lightbox over the list you are reading. */
    private static _renderFiles(files?: Attachment[]) {
        if (!files?.length) {
            return nothing;
        }
        return html`<span class="row-trailing">
            ${files.map(
                (a) =>
                    html`<cv-attach-chip
                        .src=${a.isImage ? (a.preview ?? a.dataUrl) : iconUrl(a.name)}
                        .label=${a.name}
                        title=${a.name}
                    ></cv-attach-chip>`,
            )}
        </span>`;
    }

    private _drop(uuid: string): void {
        this.dispatchEvent(
            new CustomEvent('drop-queued', { detail: { uuid }, bubbles: true, composed: true }),
        );
    }

    /** Hand the message back to the composer to be fixed. The list closes with it: what you are
     *  editing is down there now, and a popover over the composer would be in the way. */
    private _edit(uuid: string): void {
        this.dispatchEvent(
            new CustomEvent('edit-queued', { detail: { uuid }, bubbles: true, composed: true }),
        );
        this._open = false;
    }

    /** The item is the click target, so its buttons have to stop the event reaching it — pressing
     *  remove would otherwise open the editor on the entry it is deleting. */
    private _onAction(e: Event, run: () => void): void {
        e.stopPropagation();
        run();
    }

    private _clear = (): void => {
        this.dispatchEvent(new CustomEvent('clear-queue', { bubbles: true, composed: true }));
    };

    /** Words, not the bin the rows carry: the two sit a few pixels apart and differ only in what
     *  they take, so with the same glyph the only thing telling them apart would be position —
     *  and that mistake costs the whole queue instead of one message. Red inline rather than
     *  through a class: this renders into cv-popover-list's shadow, which a rule written here
     *  would not reach. */
    private _renderClear() {
        return html`<fluent-button
            appearance="subtle"
            size="small"
            style="background: var(--colorStatusDangerBackground3, #c50f1f); color: #fff"
            title="Remove every queued message"
            @click=${this._clear}
            >Clear all</fluent-button
        >`;
    }

    override render() {
        const n = this.messages.length;
        if (n === 0) {
            return nothing;
        }

        const label = `${n} queued message${n === 1 ? '' : 's'}`;
        return html`<fluent-button
                class="chip"
                appearance="subtle"
                size="small"
                icon-only
                title=${label}
                aria-label=${label}
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
            <cv-popover-list
                ?hidden=${!this._open}
                subtleActive
                .header=${html`<span>Not sent yet (${n})</span>${this._renderClear()}`}
                .items=${this.messages}
                .isGrouped=${(item: unknown) => !!(item as QueuedMessage).groupId}
                .renderRow=${(item: unknown, selected: boolean) =>
                    this._renderRow(item as QueuedMessage, selected)}
                @select=${(e: CustomEvent<{ item: unknown }>) =>
                    this._edit((e.detail.item as QueuedMessage).uuid)}
            ></cv-popover-list>`;
    }

    /** One queued message. The row shell — click, hover, ↑/↓ — belongs to cv-popover-list; this is
     *  the content, plus the two actions, which stop the click reaching that shell so removing an
     *  entry does not also open it for editing. */
    private _renderRow(m: QueuedMessage, selected: boolean) {
        // .row-label and .row-trailing are cv-popover-list's own row classes — the ones every
        // other caller's renderRow uses, and the reason this needs no styles of its own. The
        // ordinal that used to lead the row is gone with them: the entries are already in a list,
        // in order, and a column of numbers said what their position already did.
        //
        // The actions show on the active row only, which is the hover-reveal the rest of the chat
        // does with CSS: that needs a rule keyed off the row, and a rule written here would not
        // reach markup rendering into cv-popover-list's shadow. `selected` is the same thing —
        // the list sets it on mouseenter as well as on arrow keys. visibility rather than display,
        // so the row does not resize as the pointer crosses it.
        const actions = `visibility: ${selected ? 'visible' : 'hidden'}`;
        return html`<div class="row-label">
                ${CvQueueChip._renderItemText(m.text)} ${CvQueueChip._renderFiles(m.attachments)}
            </div>
            <span class="row-trailing" style=${actions}>
                <fluent-button
                    appearance="subtle"
                    size="small"
                    icon-only
                    title="Edit in the composer"
                    aria-label="Edit in the composer"
                    @click=${(e: Event) => this._onAction(e, () => this._edit(m.uuid))}
                    >${unsafeHTML(Edit16Regular)}</fluent-button
                >
                <fluent-button
                    appearance="subtle"
                    size="small"
                    icon-only
                    style="color: var(--colorStatusDangerForeground1, #d13438)"
                    title="Remove from queue"
                    aria-label="Remove from queue"
                    @click=${(e: Event) => this._onAction(e, () => this._drop(m.uuid))}
                    >${unsafeHTML(Delete16Regular)}</fluent-button
                >
            </span>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-queue-chip': CvQueueChip;
    }
}

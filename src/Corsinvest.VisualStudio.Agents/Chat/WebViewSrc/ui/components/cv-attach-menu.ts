/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// A plus, not a paperclip or an @: the menu holds three different actions, and any of those glyphs
// would promise one of them. The plus promises nothing and lets the list say what is on offer.
import Add16Regular from '@fluentui/svg-icons/icons/add_16_regular.svg';
// Each item wears the glyph of the key that does the same thing, so the menu teaches @ and /.
import Attach16Regular from '@fluentui/svg-icons/icons/attach_16_regular.svg';
import Mention16Regular from '@fluentui/svg-icons/icons/mention_16_regular.svg';
import SlashForward16Regular from '@fluentui/svg-icons/icons/slash_forward_16_regular.svg';
import { iconStyles, iconTriggerStyles, tooltipStyles } from '../styles/shared';

/**
 * The input toolbar's one "put something in this message" button, built on `<fluent-menu>`:
 * attach a file from disk, reference one from the workspace (`@`), or run a command (`/`).
 * No "Add active file": cv-ide-context-badge covers it and the host
 * auto-injects the file path / selection into every prompt. Shadow DOM.
 *
 * No accent colour on the trigger: in this row colour means state (the gauge's bands, the mic
 * while recording, send when there is something to send), and a permanently green plus says
 * nothing while competing with the ones that do.
 */
@customElement('cv-attach-menu')
export class CvAttachMenu extends LitElement {
    static override styles = [
        iconStyles,
        iconTriggerStyles,
        tooltipStyles,
        css`
            :host {
                display: inline-flex;
            }
            /* Size the list to its longest item. fluent-menu's positioning layer
             * otherwise pins the list to the trigger (the ~28px button), which
             * squeezes it and clips "Reference a workspace file". */
            fluent-menu-list {
                width: max-content !important;
                min-width: 200px !important;
                max-width: none !important;
            }
            fluent-menu-item {
                white-space: nowrap;
            }
            /* The item lays its label in a grid content column; let it size to the text. */
            fluent-menu-item::part(content) {
                overflow: visible;
                text-overflow: clip;
            }
            /* Center the 16px icon in the item's 20px start cell — Fluent's default
             * hugs the cell edge, which looks too tight at compact density. */
            fluent-menu-item [slot='start'] {
                display: inline-flex;
                align-items: center;
                justify-content: center;
            }
            /* Wraps the glyph so it can be the tooltip's anchor while the button stays the menu's.
               A real box (not display:contents) — an anchor has to be laid out to be anchored to. */
            .tip-anchor {
                display: inline-flex;
            }
        `,
    ];

    private _onUpload = (): void => {
        this.dispatchEvent(new CustomEvent('pick-file', { bubbles: true, composed: true }));
    };

    /** "Reference a workspace file": insert "@" in the prompt to open the workspace file list. */
    private _onAddContent = (): void => {
        this.dispatchEvent(new CustomEvent('add-mention', { bubbles: true, composed: true }));
    };

    /** "Slash command": the full palette with its own search box — the mouse path to the commands,
     *  which typing `/` reaches from the keyboard. */
    private _onCommands = (): void => {
        this.dispatchEvent(new CustomEvent('open-commands', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            <fluent-menu>
                <!-- A fluent-button, not the fluent-menu-button the docs show in this slot: that
                     one always draws a chevron beside the glyph, which on a toolbar icon is a
                     second symbol for what the plus already says. The id is load-bearing —
                     fluent-menu-list anchors itself to --menu-trigger, and the trigger's
                     anchor-name comes from its id, so any other id opens the list at 0,0. -->
                <fluent-button
                    id="menu-trigger"
                    slot="trigger"
                    class="trigger"
                    appearance="subtle"
                    shape="rounded"
                    size="small"
                    icon-only
                >
                    <!-- The tooltip anchors to this span, not to the button: fluent-tooltip writes
                         anchor-name onto whatever it points at, and on the button that overwrote
                         the name fluent-menu-list needs — the list then opened at 0,0. -->
                    <span id="attach-tip" class="tip-anchor">${unsafeHTML(Add16Regular)}</span>
                </fluent-button>
                <fluent-menu-list>
                    <!-- "Attach" against "Reference" names the real difference between the two:
                         one embeds the file's content, the other only points at a path. The
                         ellipsis marks the one that opens a dialog. -->
                    <fluent-menu-item @click=${this._onUpload}>
                        <span slot="start">${unsafeHTML(Attach16Regular)}</span>
                        Attach a file…
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onAddContent}>
                        <span slot="start">${unsafeHTML(Mention16Regular)}</span>
                        Reference a workspace file
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onCommands}>
                        <span slot="start">${unsafeHTML(SlashForward16Regular)}</span>
                        Slash command
                    </fluent-menu-item>
                </fluent-menu-list>
            </fluent-menu>
            <!-- Outside the menu: inside it the tooltip would be a menu child, and fluent-menu lays
                 out only its trigger and its list. Anchored to the same id the list uses — one
                 names the anchor, the other looks the element up, and they don't collide. -->
            <!-- positioning=after, like the mic and the file chip along this row: the button is at
                 the left end of the toolbar, so opening before it would land off the pane.
                 One word, no tip-* classes: the list under the button already says what is on
                 offer, so the tooltip only has to name the button. -->
            <fluent-tooltip anchor="attach-tip" positioning="after">Add</fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-attach-menu': CvAttachMenu;
    }
}

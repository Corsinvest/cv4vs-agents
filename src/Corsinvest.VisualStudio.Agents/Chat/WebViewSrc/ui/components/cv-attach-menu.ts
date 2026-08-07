/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
// A plus, not a paperclip: the menu offers "Add content" as well as a file upload, so it means
// "add something to the message" rather than strictly "attach a file".
import Add16Regular from '@fluentui/svg-icons/icons/add_16_regular.svg';
import ArrowUpload16Regular from '@fluentui/svg-icons/icons/arrow_upload_16_regular.svg';
import DocumentText16Regular from '@fluentui/svg-icons/icons/document_text_16_regular.svg';
import { iconStyles, iconTriggerStyles, tooltipStyles } from '../styles/shared';

/**
 * Attach-actions button in the input toolbar, built on `<fluent-menu>`.
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
             * squeezes it and clips "Upload from computer". */
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

    /** "Add content": insert "@" in the prompt to open the workspace file list. */
    private _onAddContent = (): void => {
        this.dispatchEvent(new CustomEvent('add-mention', { bubbles: true, composed: true }));
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
                    <fluent-menu-item @click=${this._onUpload}>
                        <span slot="start">${unsafeHTML(ArrowUpload16Regular)}</span>
                        Upload from computer
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onAddContent}>
                        <span slot="start">${unsafeHTML(DocumentText16Regular)}</span>
                        Add content
                    </fluent-menu-item>
                </fluent-menu-list>
            </fluent-menu>
            <!-- Outside the menu: inside it the tooltip would be a menu child, and fluent-menu lays
                 out only its trigger and its list. Anchored to the same id the list uses — one
                 names the anchor, the other looks the element up, and they don't collide. -->
            <!-- positioning=before, like the slash trigger next to it: opening above would land on
                 the placeholder, and beside the button is the only direction with nothing under it.
                 tip-desc, not tip-action: alone, the smaller action size reads as a footnote to
                 something that isn't there. -->
            <fluent-tooltip anchor="attach-tip" positioning="before">
                <span class="tip-desc">A file from disk, or a path from the workspace</span>
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-attach-menu': CvAttachMenu;
    }
}

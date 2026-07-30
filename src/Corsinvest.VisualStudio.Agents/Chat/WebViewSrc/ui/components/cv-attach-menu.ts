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
import { iconStyles, iconButtonStyles, tooltipStyles } from '../styles/shared';

/**
 * Attach-actions button in the input toolbar, built on `<fluent-menu>`.
 * No "Add active file": cv-ide-context-badge covers it and the host
 * auto-injects the file path / selection into every prompt. Shadow DOM.
 */
@customElement('cv-attach-menu')
export class CvAttachMenu extends LitElement {
    static override styles = [
        iconStyles,
        iconButtonStyles,
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
            /* Attach trigger: green, at the shared 14px. */
            .icon-btn {
                color: var(--colorPaletteGreenForeground1);
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
                <button id="attach-trigger" slot="trigger" class="icon-btn" type="button">
                    ${unsafeHTML(Add16Regular)}
                </button>
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
            <!-- Outside the menu: inside it the tooltip would be a menu child, and fluent-menu
                 lays out only its trigger and its list. -->
            <fluent-tooltip anchor="attach-trigger" positioning="above-start"
                >Add files or content</fluent-tooltip
            >
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-attach-menu': CvAttachMenu;
    }
}

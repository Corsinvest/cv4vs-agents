/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import BranchFork16Regular from '@fluentui/svg-icons/icons/branch_fork_16_regular.svg';
import ChevronDown16Regular from '@fluentui/svg-icons/icons/chevron_down_16_regular.svg';
import ChevronUp16Regular from '@fluentui/svg-icons/icons/chevron_up_16_regular.svg';
import { iconStyles } from '../styles/shared';
import { formatTimeAgo, formatAbsolute } from '../../core/time';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import type { ForkNotification } from '../../core/types';
import './cv-copy-btn';

/**
 * The message actions row (bottom of a user/assistant bubble, revealed on hover by the parent):
 * Copy + optional Fork + optional "x ago" timestamp (absolute date/time in its tooltip). Small
 * bare-button icons — the icon-action standard shared with cv-copy-btn. The anchor for future
 * pin/listen buttons.
 */
@customElement('cv-msg-actions')
export class CvMsgActions extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: flex;
                align-items: center;
                gap: 4px;
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
            .ts {
                font-size: 0.82em;
                color: var(--colorNeutralForeground3);
                cursor: default;
            }
        `,
    ];

    @property() text = '';
    @property() role: 'user' | 'assistant' = 'assistant';
    @property() uuid = '';
    @property({ type: Number }) timestamp = 0;
    @property({ type: Boolean }) canFork = false;
    /** Show the expand/reduce toggle (long user messages); state in `expanded`. */
    @property({ type: Boolean }) canExpand = false;
    @property({ type: Boolean }) expanded = false;

    private _onFork = (e: Event): void => {
        e.stopPropagation();
        if (!this.uuid) {
            return;
        }
        bridge.sendNotification<ForkNotification>(Msg.fromWebView.session.fork, {
            messageUuid: this.uuid,
        });
    };

    // Expand toggles the parent message's state; bubble a composed event so cv-message handles it.
    private _onExpand = (e: Event): void => {
        e.stopPropagation();
        this.dispatchEvent(new CustomEvent('toggle-expand', { bubbles: true, composed: true }));
    };

    override render() {
        return html`
            <cv-copy-btn .text=${this.text} title="Copy message"></cv-copy-btn>
            ${
                this.canFork
                    ? html`<button
                          class="icon-btn"
                          title="Fork conversation from here"
                          @click=${this._onFork}
                      >
                          ${unsafeHTML(BranchFork16Regular)}
                      </button>`
                    : nothing
            }
            ${
                this.canExpand
                    ? html`<button
                          class="icon-btn"
                          title=${this.expanded ? 'Reduce' : 'Expand'}
                          @click=${this._onExpand}
                      >
                          ${unsafeHTML(this.expanded ? ChevronUp16Regular : ChevronDown16Regular)}
                      </button>`
                    : nothing
            }
            ${
                this.timestamp > 0
                    ? html`<span class="ts" title=${formatAbsolute(this.timestamp)}
                          >${formatTimeAgo(this.timestamp)}</span
                      >`
                    : nothing
            }
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-msg-actions': CvMsgActions;
    }
}

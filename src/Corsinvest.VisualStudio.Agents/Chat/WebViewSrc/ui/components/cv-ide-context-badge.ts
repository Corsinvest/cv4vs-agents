/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import EyeOff16Regular from '@fluentui/svg-icons/icons/eye_off_16_regular.svg';
import { state as appState } from '../../core/state';
import type { IdeContextNotification, SetSendSelectionNotification } from '../../core/types';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { iconUrl } from '../../core/icon-url';
import { iconStyles, tooltipStyles } from '../styles/shared';

/**
 * Compact "context chip" above the chat textarea, showing the file that rides along with each
 * prompt. The whole chip is the toggle. Hidden when there's no active document.
 * Shadow DOM + static styles.
 */
@customElement('cv-ide-context-badge')
export class CvIdeContextBadge extends LitElement {
    static override styles = [
        iconStyles,
        tooltipStyles,
        css`
            /* inline-flex, not the default inline: an inline host sits on the text baseline, which
               left the chip a pixel low against the icon buttons beside it. */
            :host {
                display: inline-flex;
                align-items: center;
            }
            /* Single clickable chip = the share toggle (the file is already open in VS). */
            .badge {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                min-width: 0;
                /* The ceiling belongs to the whole chip, not to the name: capping the name alone
                   let the icon and the :24-27 range add to it, so selecting lines made the chip
                   wider than a chip without one. The name is the part that gives way. */
                max-width: 160px;
                font-size: var(--fontSizeBase100);
                color: var(--colorNeutralForeground3);
                background: none;
                border: none;
                padding: 2px 4px;
                cursor: pointer;
                border-radius: var(--borderRadiusSmall);
                font-family: inherit;
            }
            .badge:hover {
                background: color-mix(in srgb, var(--colorNeutralForeground1) 8%, transparent);
            }
            .badge:hover .name {
                color: var(--colorNeutralForeground1);
            }
            .badge.is-disabled {
                opacity: 0.55;
            }
            /* Only rendered while paused, so there is one colour to give it. */
            .eye {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
                color: var(--colorNeutralForeground3);
            }
            .eye svg {
                width: 14px;
                height: 14px;
                display: block;
            }
            /* A ceiling, not a width: a short name takes the room it needs and a long one stops
               here. The ellipsis goes at the START — a truncated name keeps its extension and last
               words, which is what tells one file from another, while the head is usually a shared
               prefix. rtl flips which end is cut; bdi keeps the text itself in reading order
               (without it "Chat.cs" would render as "sc.tahC"). */
            .name {
                font-family: var(--fontFamilyMonospace);
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                /* No max of its own — it shrinks to whatever the chip's ceiling leaves once the
                   icon and the range have taken theirs. min-width:0 is what allows a flex item
                   to go below its content width at all. */
                min-width: 0;
                direction: rtl;
                text-align: left;
            }
            .file-icon {
                flex-shrink: 0;
                display: block;
            }
            .info {
                flex-shrink: 0;
                color: var(--colorNeutralForeground3);
                white-space: nowrap;
            }
        `,
    ];

    @state() private _ctx: IdeContextNotification | null = appState.ideContext;
    @state() private _enabled = appState.ideContextEnabled;

    private _offCtx?: () => void;
    private _offEnabled?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._offCtx = appState.on('ideContext', (v) => {
            this._ctx = v;
        });
        this._offEnabled = appState.on('ideContextEnabled', (v) => {
            this._enabled = v;
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offCtx?.();
        this._offEnabled?.();
    }

    private _onToggleEye = (e: Event): void => {
        e.stopPropagation();
        const enabled = !this._enabled;
        appState.ideContextEnabled = enabled;
        // Flip the session's SendSelection: the host gates OnEditorContextChangedForChat on it,
        // so closing the eye stops the editor selection reaching this chat (and re-enabling
        // re-emits the current one).
        bridge.sendNotification<SetSendSelectionNotification>(
            Msg.fromWebView.cli.setSendSelection,
            { enabled },
        );
    };

    override render() {
        const ctx = this._ctx;
        // No active document → nothing to show.
        if (!ctx?.filePath) {
            return nothing;
        }
        // The whole chip is the toggle: the file is already open in VS (it's why
        // it's here), so there's no "open file" action — only share on/off.
        // Paused dims the chip rather than hiding it, so it can be switched back on.
        const cls = `badge${this._enabled ? '' : ' is-disabled'}`;
        // Editor-style `:start-end` range, shown only for a real selection
        // (a bare open file carries no lines). Matches the in-bubble chip.
        const lineInfo = ctx.hasSelection ? `:${ctx.startLine}-${ctx.endLine}` : '';

        return html`
            <button id="ide-badge" class=${cls} type="button" @click=${this._onToggleEye}>
                <!-- Only when paused. Sharing is the normal state and the file icon and name
                     already say which file is in play; a second glyph beside them adds nothing.
                     Not being sent is the exception, and the one worth marking — otherwise the
                     chip reads as "this file is going along" when it isn't. -->
                ${
                    this._enabled
                        ? nothing
                        : html`<span class="eye">${unsafeHTML(EyeOff16Regular)}</span>`
                }
                <img class="file-icon" src=${iconUrl(ctx.fileName)} width="16" height="16" alt="" />
                <span class="name"><bdi>${ctx.fileName}</bdi></span>
                ${lineInfo ? html`<span class="info">${lineInfo}</span>` : nothing}
            </button>
            <!-- The chip truncates the name, so the tooltip is where it can be read in full. -->
            <fluent-tooltip anchor="ide-badge" positioning="above-start">
                <span class="tip-name">${ctx.fileName}${lineInfo}</span>
                <span class="tip-action">
                    ${
                        this._enabled
                            ? 'Goes with every message — click to stop'
                            : 'Is not sent — click to include it'
                    }
                </span>
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-ide-context-badge': CvIdeContextBadge;
    }
}

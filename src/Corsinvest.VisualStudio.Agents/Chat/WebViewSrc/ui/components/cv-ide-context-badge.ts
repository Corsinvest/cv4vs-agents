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
import { iconStyles } from '../styles/shared';

/**
 * Compact "context chip" above the chat textarea, showing the file that rides along with each
 * prompt. The whole chip is the toggle. Hidden when there's no active document.
 * Shadow DOM + static styles.
 */
@customElement('cv-ide-context-badge')
export class CvIdeContextBadge extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* Single clickable chip = the share toggle (the file is already open in VS). */
            .badge {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                min-width: 0;
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
            .name {
                font-family: var(--fontFamilyMonospace);
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                max-width: 140px;
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
        const title = this._enabled
            ? `${ctx.fileName} goes with every message — click to stop`
            : `${ctx.fileName} is not sent — click to include it`;

        return html`
            <button class=${cls} type="button" title=${title} @click=${this._onToggleEye}>
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
                <span class="name">${ctx.fileName}</span>
                ${lineInfo ? html`<span class="info">${lineInfo}</span>` : nothing}
            </button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-ide-context-badge': CvIdeContextBadge;
    }
}

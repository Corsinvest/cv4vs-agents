/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Eye16Regular from '@fluentui/svg-icons/icons/eye_16_regular.svg';
import EyeOff16Regular from '@fluentui/svg-icons/icons/eye_off_16_regular.svg';
import { state as appState } from '../../core/state';
import type { IdeContextNotification, SetSendSelectionNotification } from '../../core/types';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { iconUrl } from '../../core/icon-url';
import { iconStyles } from '../styles/shared';

/**
 * Compact "context chip" above the chat textarea: file name opens the file in
 * VS at the selection; eye icon toggles sharing IDE context for the session.
 * Hidden when there's no active document. Shadow DOM + static styles.
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
            .eye {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
                /* Active (sharing) = brand/azure; paused = neutral grey. */
                color: var(--colorBrandForeground1);
            }
            .badge.is-disabled .eye {
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
        // Eye OFF dims the chip rather than hiding it, so the user can re-enable.
        const cls = `badge${this._enabled ? '' : ' is-disabled'}`;
        // Editor-style `:start-end` range, shown only for a real selection
        // (a bare open file carries no lines). Matches the in-bubble chip.
        const lineInfo = ctx.hasSelection ? `:${ctx.startLine}-${ctx.endLine}` : '';
        const eyeIcon = this._enabled ? Eye16Regular : EyeOff16Regular;
        const title = this._enabled
            ? 'IDE context attached — click to stop sharing'
            : 'IDE context paused — click to share again';

        return html`
            <button class=${cls} type="button" title=${title} @click=${this._onToggleEye}>
                <span class="eye">${unsafeHTML(eyeIcon)}</span>
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

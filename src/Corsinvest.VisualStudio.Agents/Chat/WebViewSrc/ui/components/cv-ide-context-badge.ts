/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import EyeOff16Regular from '@fluentui/svg-icons/icons/eye_off_16_regular.svg';
import Bookmark16Regular from '@fluentui/svg-icons/icons/bookmark_16_regular.svg';
import CodeBlock16Regular from '@fluentui/svg-icons/icons/code_block_16_regular.svg';
import { state as appState } from '../../core/state';
import { StateSubscriptions } from '../../core/state-subscriptions';
import type { IdeContextNotification, SetSendSelectionNotification } from '../../core/types';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { iconUrl } from '../../core/icon-url';
import { displayPathUi } from '../paths';
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
            /* The one thing in the row that gives way: min-width:0 lets a flex item go below its
               content width, which is what makes the name truncate instead of pushing the row
               wider than the composer. */
            :host {
                display: inline-flex;
                align-items: center;
                min-width: 0;
                flex-shrink: 1;
            }
            /* Single clickable chip = the share toggle (the file is already open in VS). Fluent's
               own button so its hover and focus come with it; ours are the metrics and the cap. */
            .badge {
                /* The ceiling belongs to the whole chip, not to the name: capping the name alone
                   let the icon and the :24-27 range add to it, so selecting lines made the chip
                   wider than a chip without one. The name is the part that gives way. */
                max-width: 160px;
                min-width: 0;
                font-size: var(--fontSizeBase100);
                padding-inline: 6px;
            }
            .badge::part(content) {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                min-width: 0;
                overflow: hidden;
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
            /* Trailing "what goes out" glyph. Dimmer than the name it follows: it qualifies the
               chip, it is not the point of it. */
            .what {
                flex-shrink: 0;
                display: inline-flex;
                align-items: center;
                color: var(--colorNeutralForeground3);
                opacity: 0.8;
            }
            .what svg {
                width: 13px;
                height: 13px;
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
    /** Mirrored rather than read at render time because Options → Apply replaces the whole `ui`
     *  block while the chat is open, and the chip has to follow it. */
    @state() private _withText = appState.ui.sendSelectionText;

    private readonly _subs = new StateSubscriptions(this);

    constructor() {
        super();
        this._subs.on('ideContext', (v) => {
            this._ctx = v;
        });
        this._subs.on('ideContextEnabled', (v) => {
            this._enabled = v;
        });
        this._subs.on('ui', (v) => {
            this._withText = v.sendSelectionText;
        });
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
        // The option alone doesn't decide it: with no selection there is no code to attach, so an
        // open file stays a bookmark whatever the setting says.
        const withCode = this._withText && ctx.hasSelection;

        return html`
            <fluent-button
                id="ide-badge"
                class=${cls}
                appearance="subtle"
                size="small"
                @click=${this._onToggleEye}
            >
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
                <!-- An indicator, not a second button: the chip has one action and it is the eye.
                     Dropped while paused — nothing is going out to describe. -->
                ${
                    this._enabled
                        ? html`<span class="what"
                              >${unsafeHTML(withCode ? CodeBlock16Regular : Bookmark16Regular)}</span
                          >`
                        : nothing
                }
            </fluent-button>
            <!-- The path, not the bare name the chip already shows: it says WHICH file, through
                 displayPathUi so it follows "Show relative paths" like the tool rows and falls back
                 to the full path outside the workdir. That line is the data, so it keeps tip-name;
                 the second is where the trailing glyph gets its words — an icon on its own is a
                 riddle. Not "click to stop": clicking a toggle is what a toggle is for. -->
            <fluent-tooltip anchor="ide-badge" positioning="after">
                <span class="tip-name">${displayPathUi(ctx.filePath)}${lineInfo}</span>
                <span class="tip-desc"
                    >${
                        !this._enabled
                            ? 'Not sent'
                            : withCode
                              ? 'Sent with every message, selected code included'
                              : 'Sent with every message — the position, not the code'
                    }</span
                >
            </fluent-tooltip>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-ide-context-badge': CvIdeContextBadge;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import QrCode20Regular from '@fluentui/svg-icons/icons/qr_code_20_regular.svg';
import { REMOTE_CONTROL_ICON } from '../../core/commands/builtin-commands';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { state as appState } from '../../core/state';
import { iconStyles, iconTriggerStyles, tooltipStyles } from '../styles/shared';

/**
 * Remote Control indicator in the input toolbar, shown only while a bridge is up. It sits before
 * the file chip: that one takes the row's spare width and resizes with the active file's name, so
 * anything after it shifts on every editor change.
 *
 * A menu, not a plain click: asking for the link again is the frequent action and turning Remote
 * Control off is the irreversible one, and they should not share a target.
 */
@customElement('cv-remote-chip')
export class CvRemoteChip extends LitElement {
    static override styles = [
        iconStyles,
        iconTriggerStyles,
        tooltipStyles,
        css`
            :host {
                display: inline-flex;
            }
            /* Filled like the mic while recording, so "on" reads at a glance rather than as one
               more grey glyph. Brand rather than red: red in this row is already Stop and the
               recording mic, and a third permanent red would say urgency where there is none.
               No pulse — the mic earns one by being transient, this stays until switched off. */
            .trigger,
            .trigger:hover {
                background: var(--colorBrandBackground);
                color: var(--colorNeutralForegroundOnBrand);
            }
            fluent-menu-list {
                width: max-content !important;
                min-width: 200px !important;
                max-width: none !important;
            }
            fluent-menu-item {
                white-space: nowrap;
            }
            fluent-menu-item [slot='start'] {
                display: inline-flex;
                align-items: center;
                justify-content: center;
            }
            /* A laid-out box so the tooltip has something to anchor to (see the trigger below). */
            .tip-anchor {
                display: inline-flex;
            }
        `,
    ];

    @state() private accessor _status = appState.remoteControl.status;

    private _unsubscribe?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._status = appState.remoteControl.status;
        this._unsubscribe = appState.on('remoteControl', (v) => {
            this._status = v.status;
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._unsubscribe?.();
    }

    private _onShowLink = (): void => {
        this.dispatchEvent(new CustomEvent('show-remote-link', { bubbles: true, composed: true }));
    };

    private _onTurnOff = (): void => {
        bridge.sendNotification(Msg.fromWebView.cli.setRemoteControl, { enabled: false });
    };

    override render() {
        if (this._status !== 'connected') {
            return nothing;
        }
        return html`
            <fluent-menu>
                <!-- id must be menu-trigger: fluent-menu-list anchors to --menu-trigger, whose
                     anchor-name comes from the id. Any other id opens the list at 0,0. -->
                <fluent-button
                    id="menu-trigger"
                    slot="trigger"
                    class="trigger"
                    aria-label="Remote control"
                    appearance="subtle"
                    shape="rounded"
                    size="small"
                    icon-only
                >
                    <!-- Anchor on the span, not the button: fluent-tooltip writes anchor-name onto
                         its target, and on the button that overwrites the one the list needs. -->
                    <span id="remote-tip" class="tip-anchor"
                        >${unsafeHTML(REMOTE_CONTROL_ICON)}</span
                    >
                </fluent-button>
                <fluent-menu-list>
                    <fluent-menu-item @click=${this._onShowLink}>
                        <span slot="start">${unsafeHTML(QrCode20Regular)}</span>
                        Show link and QR code
                    </fluent-menu-item>
                    <fluent-menu-item @click=${this._onTurnOff}>
                        <span slot="start">${unsafeHTML(Dismiss16Regular)}</span>
                        Turn off Remote Control
                    </fluent-menu-item>
                </fluent-menu-list>
            </fluent-menu>
            <!-- positioning=after, like the file chip beside it: above would land on the
                 textarea and cover what is being typed. -->
            <!-- One line: the chip is only on while the feature is, so what it is worth saying is
                 what that means, not the name a menu item repeats. -->
            <fluent-tooltip anchor="remote-tip" positioning="after"
                >Driven from claude.ai/code</fluent-tooltip
            >
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-remote-chip': CvRemoteChip;
    }
}

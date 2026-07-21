/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import type { CliExitedNotification } from '../../core/types';

/**
 * Persistent error/info bar shown across the top of the chat when the CLI
 * process exits unexpectedly. Hidden by default.
 *
 * Listens to `cli_exited` and `cli_started` bridge messages.
 */
@customElement('cv-cli-banner')
export class CvCliBanner extends LitElement {
    @state() private _message = '';

    private _offExited?: () => void;
    private _offStarted?: () => void;

    // Shadow DOM (Lit default): no custom CSS — the banner is pure fluent-message-bar + fluent-link.

    override connectedCallback(): void {
        super.connectedCallback();
        this._offExited = bridge.onNotification<CliExitedNotification>(
            Msg.toWebView.cli.exited,
            (data) => {
                // Intentional exits are respawns we triggered (session switch/resume/workdir) — not a crash.
                if (data?.intentional) {
                    this._message = '';
                    return;
                }
                const code = data?.exitCode;
                this._message =
                    code != null && code !== 0
                        ? `Claude Code process exited (code ${code})`
                        : 'Claude Code process exited';
            },
        );
        this._offStarted = bridge.onNotification(Msg.toWebView.cli.started, () => {
            this._message = '';
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offExited?.();
        this._offStarted?.();
    }

    private _viewLogs = (e: Event): void => {
        e.preventDefault();
        bridge.sendNotification(Msg.fromWebView.open.ideOutputWindow, {});
    };

    override render() {
        if (!this._message) {
            return nothing;
        }
        return html`
            <fluent-message-bar intent="error" shape="square">
                <span>${this._message}</span>
                <fluent-button
                    slot="actions"
                    appearance="transparent"
                    size="small"
                    @click=${this._viewLogs}
                    >View logs</fluent-button
                >
            </fluent-message-bar>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-cli-banner': CvCliBanner;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement } from 'lit/decorators.js';
import { state as appState } from '../../core/state';

/**
 * Empty-chat welcome screen: logo, title, version, a "get started" line and the
 * project/company links. Shown by cv-app when the conversation has no entries.
 * Shadow DOM; the external links use target=_blank — WebView2's
 * NewWindowRequested opens them in the system browser (WebViewBridge), so no
 * click handler is needed.
 */
@customElement('cv-welcome')
export class CvWelcome extends LitElement {
    static override styles = css`
        :host {
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 10px;
            min-height: 60vh;
            text-align: center;
            color: var(--colorNeutralForeground3);
            animation: fadeIn 0.5s ease both;
        }
        /* Scales with the panel width (the chat tool window varies narrow↔wide).
         * The source PNG is 500x500 so it stays crisp across this range. */
        .logo {
            width: clamp(80px, 28vw, 144px);
            height: auto;
            aspect-ratio: 1;
            opacity: 0.95;
        }
        .title {
            font-size: clamp(16px, 4.5vw, 22px);
            font-weight: 600;
            color: var(--colorNeutralForeground1);
        }
        .version {
            font-size: clamp(11px, 2.6vw, 13px);
            color: var(--colorNeutralForeground4);
            font-variant-numeric: tabular-nums;
        }
        .subtitle {
            font-size: clamp(12px, 3vw, 14px);
            color: var(--colorNeutralForeground3);
            margin-top: 2px;
        }
        .links {
            display: flex;
            align-items: center;
            gap: 6px;
            font-size: var(--fontSizeBase200);
            margin-top: 2px;
        }
        .dot {
            color: var(--colorNeutralForeground4);
        }
        .copyright {
            font-size: 11px;
            color: var(--colorNeutralForeground4);
            margin-top: 2px;
        }
        @keyframes fadeIn {
            from {
                opacity: 0;
                transform: translateY(6px);
            }
            to {
                opacity: 1;
                transform: translateY(0);
            }
        }
    `;

    override render() {
        return html`
            <img class="logo" src="images/plugin-logo.png" alt="" />
            <div class="title">cv4vs Agents</div>
            ${
                appState.ui.appVersion
                    ? html`<div class="version">v${appState.ui.appVersion}</div>`
                    : nothing
            }
            <div class="subtitle">Ask anything to get started</div>
            <div class="links">
                <fluent-link
                    href="https://github.com/Corsinvest/cv4vs-agents"
                    target="_blank"
                    rel="noopener noreferrer"
                    >GitHub</fluent-link
                >
                <span class="dot">·</span>
                <fluent-link
                    href="https://www.corsinvest.it"
                    target="_blank"
                    rel="noopener noreferrer"
                    >Corsinvest Srl</fluent-link
                >
            </div>
            <div class="copyright">© Corsinvest Srl 2026</div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-welcome': CvWelcome;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Copy16Regular from '@fluentui/svg-icons/icons/copy_16_regular.svg';
import Checkmark16Regular from '@fluentui/svg-icons/icons/checkmark_16_regular.svg';
import { iconStyles } from '../styles/shared';

/**
 * Reusable copy-to-clipboard icon button (wraps `<fluent-button>`, shows a
 * checkmark for ~1.2s after click). Text comes from `text`, lazily from a
 * sibling `<pre>` (`frompre`) / previous element (`fromprev`), or `getBlob`
 * for binary payloads written as a `ClipboardItem` (pasteable as a real image).
 *
 * Shadow DOM + static styles. The host is `display:contents` (no layout box —
 * the inner button participates in the parent flex/grid). Callers that need to
 * position the button (absolute hover-reveal over a <pre>/patch) reach it via
 * `::part(button)` — the shadow boundary blocks plain descendant selectors.
 */
@customElement('cv-copy-btn')
export class CvCopyBtn extends LitElement {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* Copied state: tint the checkmark green for a clearer "done" signal. */
            fluent-button.copied svg {
                color: var(--colorPaletteGreenForeground1);
                fill: var(--colorPaletteGreenForeground1);
            }
        `,
    ];

    @property() text = '';
    @property() override title = 'Copy';
    /** When truthy, read text from the previous sibling at click time. */
    @property({ attribute: 'fromprev' }) fromPrev = '';
    /** When truthy, read text from a sibling `<pre>` in the same parent (code block). */
    @property({ attribute: 'frompre' }) fromPre = '';
    /** Lazy blob factory for non-text copy (images, …); takes precedence over `text`. */
    @property({ attribute: false }) getBlob?: () => Promise<Blob>;

    @state() private _copied = false;

    private _onClick = async (e: Event): Promise<void> => {
        e.stopPropagation();
        try {
            if (this.getBlob) {
                const blob = await this.getBlob();
                await navigator.clipboard.write([new ClipboardItem({ [blob.type]: blob })]);
            } else {
                let text = this.text;
                if (!text && this.fromPrev) {
                    text = this.previousElementSibling?.textContent ?? '';
                }
                if (!text && this.fromPre) {
                    const pre = this.parentElement?.querySelector('pre');
                    text = pre?.textContent ?? '';
                }
                await navigator.clipboard.writeText(text);
            }
            this._copied = true;
            setTimeout(() => (this._copied = false), 1200);
        } catch (err) {
            // eslint-disable-next-line no-console
            console.warn('[cv-copy-btn] copy failed', err);
        }
    };

    override render() {
        return html`
            <fluent-button
                part="button"
                appearance="subtle"
                size="small"
                icon-only
                class=${this._copied ? 'copied' : ''}
                title=${this._copied ? 'Copied' : this.title}
                @click=${this._onClick}
            >
                ${unsafeHTML(this._copied ? Checkmark16Regular : Copy16Regular)}
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-copy-btn': CvCopyBtn;
    }
}

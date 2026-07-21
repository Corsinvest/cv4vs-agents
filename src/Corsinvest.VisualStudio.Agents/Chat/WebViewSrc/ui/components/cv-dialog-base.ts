/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement } from 'lit';
import { property, query } from 'lit/decorators.js';

/**
 * Shared base for the WebView dialogs. Collapses the open/close boilerplate that was copied
 * verbatim across the 6 dialog components. The EXTERNAL lifecycle (mount/close/focus/Esc stack)
 * lives in core/dialog-host.ts and is unchanged; this base owns only the per-component internals:
 * the `open` prop, the fluent-dialog show-on-open, and the toggle->close propagation.
 *
 * Abstract (no @customElement): only the concrete dialogs register as elements. Members are
 * `protected` so each child's render() can wire `this._dlg` / `this._onDialogToggle` / `this._close`.
 */
export abstract class CvDialogBase extends LitElement {
    // NOTE: render root (Shadow vs light DOM) is NOT set here — it's per-dialog. Five dialogs use
    // Shadow DOM (their own `static styles`); cv-diff-dialog overrides createRenderRoot to light DOM
    // (diff2html markup needs global CSS). Setting it here would strip the shadow dialogs' styles.

    @property({ type: Boolean, reflect: true }) open = false;

    @query('fluent-dialog') protected _dlg?: HTMLElement & { show?: () => void; hide?: () => void };

    override updated(changed: Map<string, unknown>): void {
        // Create-on-open: the dialog element only exists while open (render returns nothing when
        // closed). After it renders, showModal so Esc/backdrop close it — @toggle then fires and we
        // propagate `close`, dropping it from the DOM (dialog-host removes it on the `close` event).
        if (changed.has('open') && this.open) {
            void this.updateComplete.then(() => this._dlg?.show?.());
        }
    }

    /** Propagate `close` (composed) to dialog-host, which removes the element and restores focus. */
    protected _close(): void {
        this.dispatchEvent(new CustomEvent('close', { bubbles: true, composed: true }));
    }

    /** Fluent v3 toggle: detail.newState 'closed' -> close. Read from CustomEvent detail because the
     *  native ToggleEvent.newState is unreliable across hosts. */
    protected _onDialogToggle = (e: Event): void => {
        const detail = (e as CustomEvent<{ newState?: string }>).detail;
        if (detail?.newState === 'closed' && this.open) {
            this._close();
        }
    };
}

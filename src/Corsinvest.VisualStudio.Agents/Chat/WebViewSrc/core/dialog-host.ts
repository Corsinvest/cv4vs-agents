/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Central open-point for the WebView dialogs. Each dialog is created on demand,
// appended to <body>, opened (open=true → the component fetches/show()s), and
// destroyed on its `close` event. Openable from anywhere (no host component),
// with a single place owning the lifecycle: return-focus capture and the Esc
// stack (dialog-focus). Reopening the same tag replaces the previous instance.
import { captureFocus, restoreFocus, pushDialog, popDialog } from './dialog-focus';
import type { LightboxRequest, DiffDialogNotification } from './types';

// The dialog custom elements are registered by the UI layer (cv-app imports them),
// not here — core/ must not import ui/. mount() only creates the already-defined tag.

function mount(tag: string, props?: Record<string, unknown>): void {
    // Replace any already-open dialog of the same tag (reopen = fresh data, last wins).
    document.querySelector(tag)?.remove();

    const el = document.createElement(tag);
    if (props) {
        Object.assign(el, props);
    }
    const returnFocus = captureFocus();
    // Full teardown, idempotent. Both paths must deregister from the Esc stack: the `close` event
    // (the dialog's own Esc/backdrop/✕) AND closeTopDialog() calling this directly (VS eats the real
    // Esc → ui_escape → closeTopDialog → close()). Removing the element alone left the entry on the
    // stack, so the next Esc was swallowed by a phantom dialog and never reached the composer menus.
    let closed = false;
    const close = (): void => {
        if (closed) {
            return;
        }
        closed = true;
        popDialog(close);
        el.remove();
        restoreFocus(returnFocus);
    };

    // The component emits `close` on toggle-closed (Esc/backdrop) or its ✕.
    el.addEventListener('close', close, { once: true });

    pushDialog(close);
    document.body.appendChild(el);
    (el as { open?: boolean }).open = true;
}

export const openUsageDialog = (): void => mount('cv-usage-dialog');
export const openStatsDialog = (): void => mount('cv-stats-dialog');
export const openContextDialog = (): void => mount('cv-context-dialog');
export const openPluginManagerDialog = (): void => mount('cv-plugin-manager');
export const openLightbox = (req: LightboxRequest): void => mount('cv-lightbox', { req });
export const openDiffDialog = (req: DiffDialogNotification): void =>
    mount('cv-diff-dialog', { req });

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
import type { LightboxRequest, RewindPoint } from './types';

// The dialog custom elements are registered by the UI layer (cv-app imports them),
// not here — core/ must not import ui/. mount() only creates the already-defined tag.

// Close of the instance currently up for a tag, so a reopen tears the old one down the same way
// its own `close` event would.
const mounted = new Map<string, (keepFocus?: boolean) => void>();

function mount(tag: string, props?: Record<string, unknown>): void {
    // Replace any already-open dialog of the same tag (reopen = fresh data, last wins). Keep the
    // focus: the outgoing restore is delayed, so it would land after this one is up and steal it.
    mounted.get(tag)?.(true);

    const el = document.createElement(tag);
    if (props) {
        Object.assign(el, props);
    }
    const returnFocus = captureFocus();
    // Full teardown, idempotent. Both paths must deregister from the Esc stack: the `close` event
    // (the dialog's own Esc/backdrop/✕) AND closeTopDialog() calling this directly (VS eats the real
    // Esc → ui_escape → closeTopDialog → close()). Removing the element without popping leaves a
    // phantom entry that swallows the next Esc before it reaches the composer menus.
    let closed = false;
    // Arg is ours, not the listener's: `close` is also an event handler, so guard on `true`.
    const close = (keepFocus?: unknown): void => {
        if (closed) {
            return;
        }
        closed = true;
        popDialog(close);
        if (mounted.get(tag) === close) {
            mounted.delete(tag);
        }
        el.remove();
        if (keepFocus !== true) {
            restoreFocus(returnFocus);
        }
    };

    // The component emits `close` on toggle-closed (Esc/backdrop) or its ✕.
    el.addEventListener('close', close, { once: true });

    pushDialog(close);
    mounted.set(tag, close);
    document.body.appendChild(el);
    (el as { open?: boolean }).open = true;
}

export const openUsageDialog = (): void => mount('cv-usage-dialog');
export const openStatsDialog = (): void => mount('cv-stats-dialog');
export const openContextDialog = (): void => mount('cv-context-dialog');
export const openPluginManagerDialog = (): void => mount('cv-plugin-manager');
/** The rewind points come in as a prop: the transcript belongs to cv-app, and core/ must not reach
 *  into the UI to read it. */
export const openRewindDialog = (points: RewindPoint[]): void =>
    mount('cv-rewind-dialog', { points });
export const openLightbox = (req: LightboxRequest): void => mount('cv-lightbox', { req });

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Restore focus to the dialog's trigger after it closes. Fluent v3 runs its
// own focus-restore on `hide()`, so we delay to land after it.

/** Snapshot of the currently focused element for `restoreFocus`. */
export function captureFocus(): HTMLElement | null {
    const a = document.activeElement;
    return a instanceof HTMLElement ? a : null;
}

/** Restore focus to `target` (if still connected) or fall back to the composer.
 *  Delayed (two frames + 250 ms) so Fluent's own focus-restore runs first. The
 *  fallback goes through cv-prompt.focusInput() (like init.ts), not a global
 *  querySelector for the textarea — it lives in cv-prompt's shadow. */
export function restoreFocus(target: HTMLElement | null): void {
    const refocus = () => {
        if (target && target.isConnected) {
            target.focus?.();
            return;
        }
        (document.querySelector('cv-prompt') as { focusInput?: () => void } | null)?.focusInput?.();
    };
    requestAnimationFrame(() => requestAnimationFrame(refocus));
    setTimeout(refocus, 250);
}

// Open-dialog stack (LIFO). VS eats the real Esc and routes it to the pane, so a
// modal <dialog> can't auto-close; init.ts' ui_escape handler asks the top dialog
// to close instead. A registry (not a DOM query) keeps this shadow-DOM-proof: the
// dialog's <fluent-dialog> may live in its shadow root, unreachable by a global
// querySelector — but the dialog knows how to close itself.
const openDialogs: Array<() => void> = [];

/** Register a dialog's close callback when it opens (call from `open()`). No-op if
 *  already registered, so a re-`open()` without an intervening close can't stack a
 *  duplicate (which would leave an orphan entry and misfire closeTopDialog). */
export function pushDialog(close: () => void): void {
    if (!openDialogs.includes(close)) {
        openDialogs.push(close);
    }
}

/** Deregister on close (call from `close()` / the toggle-closed handler). Safe to
 *  call when not present (idempotent) — the toggle event may fire after close(). */
export function popDialog(close: () => void): void {
    const i = openDialogs.lastIndexOf(close);
    if (i >= 0) {
        openDialogs.splice(i, 1);
    }
}

/** Close the most-recently-opened dialog. Returns true if one was open (so the
 *  caller can stop, e.g. not also fire a global Esc). */
export function closeTopDialog(): boolean {
    const close = openDialogs[openDialogs.length - 1];
    if (close) {
        close();
        return true;
    }
    return false;
}

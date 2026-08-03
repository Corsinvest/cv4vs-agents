/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Keys the host claimed for us: the WebView2 composition control has no way to hand a key to the
// browser (SendMouseInput/SendPointerInput exist, no keyboard counterpart), so ChatWebView claims
// the ones it drops and we act on them here. Only keys verified as dropped are forwarded —
// arrows, PageUp/PageDown and Ctrl+Left/Right already reach the browser and are left alone.
import type { HostKeyNotification } from '../core/types';

/** A text field the caret can be moved in. `contenteditable` is out of scope: the chat has none,
 *  and selection there needs Range work rather than an index pair. */
type TextField = HTMLInputElement | HTMLTextAreaElement;

/** Input types that carry a caret. `selectionStart` throws on number/email/date. */
const CARET_INPUT_TYPES = new Set(['text', 'search', 'url', 'tel', 'password']);

/** The focused text field, looking through shadow roots — most of the chat's inputs live in
 *  component shadow DOM, where `document.activeElement` stops at the host element. */
function focusedField(): TextField | null {
    let el: Element | null = document.activeElement;
    while (el?.shadowRoot?.activeElement) {
        el = el.shadowRoot.activeElement;
    }
    if (el instanceof HTMLTextAreaElement) {
        return el;
    }
    return el instanceof HTMLInputElement && CARET_INPUT_TYPES.has(el.type) ? el : null;
}

/** Start of the line holding `pos` (just past the previous newline, or 0). */
function lineStart(value: string, pos: number): number {
    return value.lastIndexOf('\n', pos - 1) + 1;
}

/** End of the line holding `pos` (the next newline, or the end of the text). */
function lineEnd(value: string, pos: number): number {
    const nl = value.indexOf('\n', pos);
    return nl < 0 ? value.length : nl;
}

/** Act on a key the host claimed. One case per forwarded key — an unknown one is dropped rather
 *  than guessed at, since the host only forwards what it was told to claim. */
export function applyHostKey(e: HostKeyNotification): void {
    switch (e.key) {
        case 'Home':
        case 'End':
            moveCaret(e.key === 'Home', e.ctrl, e.shift);
            break;
    }
}

/**
 * Home/End on whatever is focused: `toStart` picks the direction, `wholeText` (Ctrl) spans the
 * whole value instead of the current line, `extend` (Shift) keeps the selection anchor. With
 * nothing focused, scroll the transcript like the browser would.
 */
function moveCaret(toStart: boolean, wholeText: boolean, extend: boolean): void {
    const field = focusedField();
    if (!field) {
        scrollTranscript(toStart);
        return;
    }

    const value = field.value;
    // Which end moves: with Shift the anchor stays put and the focus end travels, so read the
    // caret from selectionEnd/Start depending on which side the selection grew from.
    const caret =
        field.selectionDirection === 'backward'
            ? (field.selectionStart ?? 0)
            : (field.selectionEnd ?? 0);

    const target = toStart
        ? wholeText
            ? 0
            : lineStart(value, caret)
        : wholeText
          ? value.length
          : lineEnd(value, caret);

    if (!extend) {
        field.setSelectionRange(target, target);
        return;
    }

    // Extending: hold the anchor (the end the selection did NOT grow from) and move the other.
    const anchor =
        field.selectionDirection === 'backward'
            ? (field.selectionEnd ?? 0)
            : (field.selectionStart ?? 0);
    field.setSelectionRange(
        Math.min(anchor, target),
        Math.max(anchor, target),
        target < anchor ? 'backward' : 'forward',
    );
}

/** No text field focused: Home/End scroll the transcript, as they would in a plain page.
 *  `#messages` is the scroller and cv-app renders into the light DOM, so a plain query finds it. */
function scrollTranscript(toTop: boolean): void {
    const el = document.querySelector('#messages');
    const target = el instanceof HTMLElement ? el : document.scrollingElement;
    if (target) {
        target.scrollTo({ top: toTop ? 0 : target.scrollHeight });
    }
}

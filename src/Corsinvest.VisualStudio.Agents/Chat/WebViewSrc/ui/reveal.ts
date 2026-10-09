/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// What the transcript only shows under the pointer or the focus is not kept in the DOM: one set of
// document listeners creates it on the way in and removes it on the way out. Kept per row, a copy
// button costs 12.6 nodes and 3.1 shadow roots, and a long transcript held hundreds of them, plus
// the actions row under every message. Nothing listens per row either: a thousand messages are a
// thousand marks, not four thousand listeners.
//
// Two marks:
//
// `data-copy`: a block that gets a copy button. `data-copy=""` copies the block's own <pre>; a
// value is the text to copy, for a block whose DOM does not hold it (a table's markdown source).
// A Lit template sets the `copyText` property instead, a reference to the string it already has
// rather than a copy in an attribute. The button lands in the block's `data-copy-slot` descendant
// when there is one, else in the block.
//
// `data-reveal`: a row that stays in the DOM empty, at the height chat.css reserves for it, and is
// filled from its `reveal` property (a function returning a Lit template) while the pointer or
// the focus is in its box. The box is the nearest BOX ancestor, the same one whose :hover fades
// the row in.
//
// Both take `tabindex="0"`, which is what lets the keyboard reach something that is not there yet:
// the mark takes the focus, its content appears, and the next Tab lands on it.

import { nothing, render } from 'lit';
import './components/cv-copy-btn';
import type { CvCopyBtn } from './components/cv-copy-btn';

type CopyBlock = HTMLElement & { copyText?: string };

let host: CopyBlock | null = null;
let btn: CvCopyBtn | null = null;

function textOf(block: CopyBlock): string {
    return block.copyText ?? block.dataset.copy ?? '';
}

/** Innermost marked block under the event, through every shadow root on the way. */
function markedBlock(e: Event): HTMLElement | null {
    return (
        e
            .composedPath()
            .find(
                (n): n is HTMLElement => n instanceof HTMLElement && n.hasAttribute('data-copy'),
            ) ?? null
    );
}

function showCopy(next: CopyBlock | null): void {
    // isConnected too: a re-render of the block's parent drops the button without the pointer
    // ever leaving.
    if (next && next === host && btn?.isConnected) {
        // A tool still running grows its output under the pointer.
        btn.text = textOf(next);
        return;
    }
    btn?.remove();
    host = next;
    btn = null;
    if (!next) {
        return;
    }
    // A new one each time, never one moved around: the "copied" tick is the button's own state
    // and would follow it onto the next block.
    btn = document.createElement('cv-copy-btn');
    btn.text = textOf(next);
    btn.fromPre = btn.text ? '' : '1';
    (next.querySelector('[data-copy-slot]') ?? next).append(btn);
}

/** The boxes whose :hover and :focus-within reveal a row in chat.css. */
const BOX = 'cv-message, .cv-response, .cv-children';

type Reveal = () => unknown;
type RevealRow = HTMLElement & { reveal?: Reveal };

let hoverBoxes: Element[] = [];
let focusBoxes: Element[] = [];
/** The rows filled right now, with the function each was last filled from. */
const shown = new Map<RevealRow, Reveal | undefined>();

/** Every box the event is inside, innermost first: a sub-agent's children sit in a response, and
 *  both rows show, as both :hover rules match. */
function boxesOf(e: Event): Element[] {
    return e.composedPath().filter((n): n is Element => n instanceof Element && n.matches(BOX));
}

/** The rows a box owns: marked, and with no nearer box in between. */
function rowsOf(box: Element): RevealRow[] {
    return [...box.querySelectorAll<RevealRow>('[data-reveal]')].filter(
        (row) => row.closest(BOX) === box,
    );
}

/** Looked up again on every event, not only when the boxes change: a response gets its row when
 *  its last block stops streaming, which can happen under a pointer that has not moved out. */
function syncRows(): void {
    const next = new Set<RevealRow>();
    for (const box of new Set([...hoverBoxes, ...focusBoxes])) {
        for (const row of rowsOf(box)) {
            next.add(row);
        }
    }
    for (const row of shown.keys()) {
        if (!next.has(row)) {
            render(nothing, row);
            shown.delete(row);
        }
    }
    for (const row of next) {
        // The owner hands over a new function whenever it re-renders: what the row shows (a
        // turn's figures landing, a queued message being read) can change in place too.
        if (!shown.has(row) || shown.get(row) !== row.reveal) {
            shown.set(row, row.reveal);
            render(row.reveal?.() ?? nothing, row);
        }
    }
}

/** Call once from init(). */
export function installReveal(): void {
    document.addEventListener('pointerover', (e) => {
        showCopy(markedBlock(e));
        hoverBoxes = boxesOf(e);
        syncRows();
    });
    document.documentElement.addEventListener('pointerleave', () => {
        showCopy(null);
        hoverBoxes = [];
        syncRows();
    });
    // No focusout: the focusin that follows it says where the focus went, and stepping from a
    // mark to the content it has just been given resolves to the same block and the same boxes.
    document.addEventListener('focusin', (e) => {
        showCopy(markedBlock(e));
        focusBoxes = boxesOf(e);
        syncRows();
    });
}

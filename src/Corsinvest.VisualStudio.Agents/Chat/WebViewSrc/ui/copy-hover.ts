/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The copy button of a block marked `data-copy`, created when the pointer enters the block and
// removed when it leaves. A button per block costs two shadow roots each for something only ever
// seen under the pointer, and a long transcript holds hundreds of them.
//
// `data-copy=""` copies the block's own <pre>; a value is the text to copy, for a block whose DOM
// does not hold it (a table's markdown source). A Lit template sets the `copyText` property
// instead, which is a reference to the string it already has rather than a copy in an attribute.
// The button lands in the block's `data-copy-slot` descendant when there is one, else in the block.

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

function show(next: CopyBlock | null): void {
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

/** Call once from init(). */
export function installCopyHover(): void {
    document.addEventListener('pointerover', (e) => show(markedBlock(e)));
    document.documentElement.addEventListener('pointerleave', () => show(null));
}

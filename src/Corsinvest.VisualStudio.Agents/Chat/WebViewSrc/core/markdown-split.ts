/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Where growing markdown can be cut. Kept out of markdown.ts, which pulls in DOMPurify and so
// needs a DOM: this is string arithmetic and stays testable under plain node --test.

/**
 * Offset of the last point in `text` that later text cannot change the meaning of — a blank line
 * outside a fenced block, or the line that closes one. Markdown separates blocks there, so
 * everything before it is settled and can be rendered once instead of on every delta.
 *
 * Returns 0 when there is no such point (a single growing paragraph, or nothing but a settled
 * prefix), which tells the caller to render the whole thing. Deliberately conservative: cutting
 * inside a construct would break the render, so a missed opportunity is the safe failure.
 *
 * Fence tracking matches closeOpenMarkdown — a line *starting* with ``` flips the state, so fences
 * appearing as content don't throw off the count.
 */
export function stableMarkdownSplit(text: string): number {
    const lines = text.split('\n');
    let inFence = false;
    let cut = 0;
    let pos = 0;
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (/^\s*```/.test(line)) {
            inFence = !inFence;
            // The line that CLOSES a fence settles the whole block: nothing later can reopen it.
            // Without this the open fence stays in the tail and every pass re-highlights all of
            // it — the cost that grows with the block, on the content most likely to be long.
            if (!inFence) {
                cut = pos + line.length + 1;
            }
        } else if (!inFence && i > 0 && line.trim() === '') {
            // Everything up to and including this blank line is final.
            cut = pos + line.length + 1;
        }
        pos += line.length + 1;
    }
    // Never hand back the whole text: the tail after the last blank line is still growing, and the
    // caller needs something left to re-render.
    return cut >= text.length ? 0 : cut;
}

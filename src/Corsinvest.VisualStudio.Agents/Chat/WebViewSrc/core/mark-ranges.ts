/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Wrap character ranges of already-highlighted HTML in <mark>, without cutting its tags.

/** Half-open [start, end) over the line's plain text. */
export type Range = { start: number; end: number };

/**
 * Entities the highlighter emits. Each stands for one character of the source, which is what the
 * ranges are counted in — advancing by their literal length would drift the offsets.
 */
const ENTITY = /^&(?:lt|gt|amp|quot|#x27|#39);/;

/**
 * `html` wrapped so every range is inside a <mark>, everything else untouched.
 *
 * A mark is closed and reopened around any tag that falls inside it: a <mark> spanning a
 * `<span>` boundary would nest the two badly, and the browser's recovery moves the text.
 *
 * Ranges must be sorted and non-overlapping — what diffWords produces.
 */
export function markRanges(html: string, ranges: readonly Range[]): string {
    if (!ranges.length) {
        return html;
    }
    let out = '';
    let pos = 0; // offset into the plain text, what the ranges are measured in
    let i = 0; // read cursor into html
    let r = 0; // range under consideration
    let open: boolean = false;

    const closeIfOpen = () => {
        if (open) {
            out += '</mark>';
            open = false;
        }
    };

    while (i < html.length) {
        while (r < ranges.length && pos >= ranges[r].end) {
            closeIfOpen();
            r++;
        }

        if (html[i] === '<') {
            const close = html.indexOf('>', i);
            const end = close < 0 ? html.length : close + 1;
            const wasOpen: boolean = open;
            closeIfOpen();
            out += html.slice(i, end);
            open = wasOpen;
            if (wasOpen) {
                out += '<mark>';
            }
            i = end;
            continue;
        }

        const inRange = r < ranges.length && pos >= ranges[r].start && pos < ranges[r].end;
        if (inRange && !open) {
            out += '<mark>';
            open = true;
        } else if (!inRange && open) {
            closeIfOpen();
        }

        const entity = html[i] === '&' ? ENTITY.exec(html.slice(i)) : null;
        const step = entity ? entity[0].length : 1;
        out += html.slice(i, i + step);
        i += step;
        pos++;
    }
    closeIfOpen();
    return out;
}

/** Ranges over the concatenated text of `segs`, one per changed segment. */
export function rangesOf(segs: readonly { text: string; changed: boolean }[]): Range[] {
    const ranges: Range[] = [];
    let at = 0;
    for (const seg of segs) {
        if (seg.changed) {
            ranges.push({ start: at, end: at + seg.text.length });
        }
        at += seg.text.length;
    }
    return ranges;
}

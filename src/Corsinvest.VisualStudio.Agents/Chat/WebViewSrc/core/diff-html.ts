/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The rows of a diff preview as one HTML string.
//
// A string and not a Lit template per row: measured on real edits, 17 of a row's 25 nodes were
// scaffolding (three <span> for the gutter and the text, five binding markers, four whitespace
// texts), and a transcript holds one preview per Edit. Here a row is one <div> holding its text
// and nothing else: diff.css draws the line number from `data-ln` and the sign from the class.

import type { Row, Seg } from './diff-rows';
import { escapeHtml } from './html';
import { highlightCode } from './lang';
import { markRanges, rangesOf } from './mark-ranges';

/** The row's text: highlighted whole and then marked, or escaped as it is when the language is
 *  unknown, which is what an unhighlighted file should look like.
 *
 *  The highlighter needs the whole line: given a changed piece alone it sees a word out of
 *  context and returns `boolean` plain, not as a keyword, so it runs first and markRanges puts
 *  the marks over its HTML. */
function textHtml(segs: readonly Seg[], lang: string): string {
    const hl = highlightCode(segs.map((s) => s.text).join(''), lang);
    return hl
        ? markRanges(hl, rangesOf(segs))
        : segs
              .map((s) => (s.changed ? `<mark>${escapeHtml(s.text)}</mark>` : escapeHtml(s.text)))
              .join('');
}

/**
 * `rows` as HTML for `unsafeHTML`. Every character of the diff reaches the string escaped: by the
 * highlighter, or by escapeHtml when there is none.
 *
 * `numbered` is false until the tool's result brings the real hunks: the fragments of the input
 * are numbered from 1, true of the fragment and false of the file, so no number is written.
 */
export function diffRowsHtml(rows: readonly Row[], lang: string, numbered: boolean): string {
    let out = '';
    for (const row of rows) {
        if (row.kind === 'hunk') {
            out += '<div class="cv-diff-hunk"></div>';
            continue;
        }
        // One gutter: a '-' exists only in the old file and a '+' only in the new, so every
        // row has exactly one number worth showing.
        const no = row.kind === 'del' ? row.oldNo : row.newNo;
        const ln = numbered && no !== null ? ` data-ln="${no}"` : '';
        out += `<div class="cv-diff-row cv-diff-${row.kind}"${ln}>${textHtml(row.segs, lang)}</div>`;
    }
    return out;
}

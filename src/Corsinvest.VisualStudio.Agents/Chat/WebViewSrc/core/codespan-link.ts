/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Inline `code` that is a file reference and nothing else — `ClientEvents.cs:208` — rendered as a
// link inside the code span.
//
// Why this exists: marked tokenizes inline code before any extension runs, so the file-link
// tokenizer in markdown.ts never sees what is inside backticks. That would be the right rule for a
// fence, and it is kept there; for an inline span it loses almost everything. Counted over 25 real
// sessions in this repo: 33 references written inside backticks against 2 written bare — the model
// backticks a filename because it IS a filename, and treating that as untouchable code inverts the
// intent.
//
// Its own module rather than a branch in markdown.ts so the rule can be tested: markdown.ts pulls
// in marked, DOMPurify and highlight.js, and node --test cannot resolve extensionless value imports
// anyway. What it needs from elsewhere is passed in — same shape as formatTimeAgo taking its clock —
// which keeps this file import-free and therefore loadable by the test runner as it stands.

import type { FileRef } from './file-links';

/** What this needs from the rest of the app: the parser, and the escaper. */
export interface CodespanDeps {
    /** file-links.parseFileRef — a ref only when it is the WHOLE token. */
    parseFileRef: (token: string, strictness: 'known-ext') => FileRef | null;
    escapeHtml: (s: unknown) => string;
}

/** The link markup for one reference, in the shape cv-message's click handler expects. */
function anchor(
    esc: CodespanDeps['escapeHtml'],
    path: string,
    label: string,
    line: number,
    end: number,
): string {
    return (
        `<a class="cv-file-link" data-file="${esc(path)}" data-line="${line}" ` +
        `data-line-end="${end}" title="Open in editor">${esc(label)}</a>`
    );
}

/**
 * The inner HTML of an inline code span: a link when the whole span is a file reference, the
 * escaped text otherwise.
 *
 * Whole span or nothing. `Foo.cs:12` is the model naming a location; `cat Foo.cs:12` is a command
 * that happens to contain one, and linking part of a command would be wrong twice over — it claims
 * the command is a file, and it splits a thing meant to be copied whole. parseFileRef already
 * enforces exactly this (`start === 0 && match === token`), so the rule is one call, not a second
 * parser that could drift from the first.
 *
 * A comma list keeps the shape prose gets: the first link carries `name:line`, the rest carry the
 * bare line numbers, so `Foo.cs:12,40` reads as one file at two places rather than as two files.
 */
export function renderCodespanInner(text: string, deps: CodespanDeps): string {
    const { parseFileRef, escapeHtml } = deps;
    const ref = parseFileRef(text, 'known-ext');
    if (!ref) {
        return escapeHtml(text);
    }
    const link = (label: string, line: number, end: number) =>
        anchor(escapeHtml, ref.path, label, line, end);
    // No line: the whole path is the link, opened at the top.
    if (ref.lines.length === 0) {
        return link(ref.path, 0, 0);
    }
    // The label is what was written, so a range stays readable as "35-48" and "#L128" keeps its
    // GitHub shape instead of being normalised to its first line.
    const written = ref.match.slice(ref.path.length);
    if (ref.lines.length === 1) {
        return link(ref.path + written, ref.lines[0], ref.ends[0]);
    }
    const parts = written.replace(/^:/, '').split(',');
    const [first, ...rest] = ref.lines;
    let html = link(`${ref.path}:${parts[0] ?? first}`, first, ref.ends[0]);
    rest.forEach((ln, i) => {
        html += ',' + link(parts[i + 1] ?? String(ln), ln, ref.ends[i + 1]);
    });
    return html;
}

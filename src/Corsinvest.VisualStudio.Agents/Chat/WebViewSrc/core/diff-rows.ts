/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Patch -> rows the preview renders. Pure, no DOM: the highlighting happens
// in the component, on the segments this file cuts.

import { structuredPatch, diffWords } from 'diff';
import { patchPathFor } from './diff';
import type { PatchHunkDto } from './generated/PatchHunkDto';

export type Seg = { text: string; changed: boolean };

export type Row = {
    kind: 'ins' | 'del' | 'ctx' | 'hunk';
    oldNo: number | null;
    newNo: number | null;
    segs: Seg[];
};

/**
 * Only word-diff a pair that is mostly the same line. A replaced block is rarely one line for
 * one, so pairing by position alone hands unrelated lines to the word-diff — which then marks
 * both of them end to end, saying nothing at all.
 */
const WORD_DIFF_THRESHOLD = 0.5;

/** Share of the shorter line's words present in both, 0..1. */
export function similarity(a: string, b: string): number {
    const wa = a.trim().split(/\s+/).filter(Boolean);
    const wb = b.trim().split(/\s+/).filter(Boolean);
    if (!wa.length || !wb.length) {
        return 0;
    }
    const pool = [...wb];
    let hit = 0;
    for (const w of wa) {
        const at = pool.indexOf(w);
        if (at >= 0) {
            pool.splice(at, 1);
            hit++;
        }
    }
    return hit / Math.min(wa.length, wb.length);
}

/** Segments of one side of a pair; `changed` marks what the other side lacks. */
function segmentsOf(oldLine: string, newLine: string, side: 'del' | 'ins'): Seg[] {
    const parts = diffWords(oldLine, newLine);
    const keep =
        side === 'del'
            ? (p: { added?: boolean }) => !p.added
            : (p: { removed?: boolean }) => !p.removed;
    return parts.filter(keep).map((p) => ({
        text: p.value,
        changed: side === 'del' ? !!p.removed : !!p.added,
    }));
}

const whole = (text: string): Seg[] => [{ text, changed: false }];

/**
 * Rows from hunks somebody else computed — the CLI's own, carried on the tool result. Preferred
 * over buildRows: those hunks know the file's real line numbers and bring the context around the
 * change, neither of which an Edit's two input fragments can give.
 */
export function rowsFromHunks(hunks: readonly PatchHunkDto[] | null | undefined): Row[] {
    const rows: Row[] = [];
    for (const hunk of hunks ?? []) {
        rows.push({ kind: 'hunk', oldNo: hunk.oldStart, newNo: hunk.newStart, segs: [] });
        let o = hunk.oldStart;
        let n = hunk.newStart;
        const lines = hunk.lines;

        for (let i = 0; i < lines.length; i++) {
            const sign = lines[i][0];
            if (sign !== '-' && sign !== '+') {
                rows.push({ kind: 'ctx', oldNo: o++, newNo: n++, segs: whole(lines[i].slice(1)) });
                continue;
            }

            // The whole run, not one pair at a time: the gate above decides which of them
            // are actually related.
            const dels: string[] = [];
            const inss: string[] = [];
            while (i < lines.length && lines[i][0] === '-') {
                dels.push(lines[i++].slice(1));
            }
            while (i < lines.length && lines[i][0] === '+') {
                inss.push(lines[i++].slice(1));
            }
            i--;

            const paired = new Set<number>();
            for (let k = 0; k < Math.min(dels.length, inss.length); k++) {
                if (similarity(dels[k], inss[k]) >= WORD_DIFF_THRESHOLD) {
                    paired.add(k);
                }
            }

            dels.forEach((t, k) =>
                rows.push({
                    kind: 'del',
                    oldNo: o++,
                    newNo: null,
                    segs: paired.has(k) ? segmentsOf(t, inss[k], 'del') : whole(t),
                }),
            );
            inss.forEach((t, k) =>
                rows.push({
                    kind: 'ins',
                    oldNo: null,
                    newNo: n++,
                    segs: paired.has(k) ? segmentsOf(dels[k], t, 'ins') : whole(t),
                }),
            );
        }
    }
    return rows;
}

/**
 * Rows from two strings we diff ourselves. Only for an edit whose result has not arrived yet:
 * the tool's input carries the two fragments and nothing else, so the hunks come out numbered
 * from 1 — true of the fragment, false of the file. The caller drops the numbers there.
 */
export function buildRows(
    oldStr: string | undefined | null,
    newStr: string | undefined | null,
    filePath: string | undefined | null,
    context: number,
    ignoreWhitespace = false,
): Row[] {
    const name = patchPathFor(filePath);
    const patch = structuredPatch(name, name, oldStr ?? '', newStr ?? '', '', '', {
        context,
        ignoreWhitespace,
        stripTrailingCr: true,
    });
    return rowsFromHunks(patch.hunks);
}

/**
 * Where an edit landed, for the jump the file link makes: the first and last line the change
 * ADDED, not the hunk's own span — that spans the context too, and selecting it would highlight
 * three untouched lines either side.
 *
 * Walks the first hunk only: MultiEdit yields several non-contiguous ones, and the first is where
 * the change starts, which is where to jump. '+' and context lines both exist in the file after
 * the edit and advance the counter; '-' lines are gone and do not. A hunk with no '+' at all —
 * a pure deletion — falls back to its own span, which is the closest thing to a location it has.
 */
export function editRangeFromHunks(
    hunks: readonly PatchHunkDto[] | null | undefined,
): [number, number] {
    const first = hunks?.[0];
    if (!first || first.newStart <= 0) {
        return [0, 0];
    }
    let line = first.newStart;
    let start = 0;
    let end = 0;
    for (const text of first.lines ?? []) {
        if (text.startsWith('-')) {
            continue;
        }
        if (text.startsWith('+')) {
            if (start === 0) {
                start = line;
            }
            end = line;
        }
        line++;
    }
    if (start > 0) {
        return [start, end];
    }
    return first.newLines > 0
        ? [first.newStart, first.newStart + first.newLines - 1]
        : [first.newStart, first.newStart];
}

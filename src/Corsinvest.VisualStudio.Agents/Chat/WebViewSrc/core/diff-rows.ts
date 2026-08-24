/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Patch -> rows the preview renders. Pure, no DOM: the highlighting happens
// in the component, on the segments this file cuts.

import { structuredPatch, diffWords } from 'diff';
import { patchPathFor } from './diff';

export type Seg = { text: string; changed: boolean };

export type Row = {
    kind: 'ins' | 'del' | 'ctx' | 'hunk';
    oldNo: number | null;
    newNo: number | null;
    segs: Seg[];
};

/**
 * Only word-diff a pair that is mostly the same line. An unrelated pair diffs
 * into "everything changed", which marks the whole row and says nothing —
 * measured on a real patch: 24 of 28 rows fully marked without this gate.
 * diff2html gates the same way (`matchWordsThreshold`).
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

    const rows: Row[] = [];
    for (const hunk of patch.hunks) {
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

            // Take the whole -/+ run: a replaced block is rarely 1-for-1, and
            // pairing the last '-' with the first '+' marks unrelated lines.
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

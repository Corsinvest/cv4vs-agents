// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildRows, similarity, editRangeFromHunks, rowsFromHunks } from '../core/diff-rows.ts';

/** Marked text of a row, "«x»" around the changed segments — readable in an assert. */
function marked(segs: { text: string; changed: boolean }[]): string {
    return segs.map((s) => (s.changed ? `«${s.text}»` : s.text)).join('');
}

test('similarity: identical lines = 1, unrelated lines = 0', () => {
    assert.equal(similarity('const x = 1', 'const x = 1'), 1);
    assert.equal(similarity('aaa bbb', 'xxx yyy'), 0);
});

test('similarity: ignores empty lines instead of dividing by zero', () => {
    assert.equal(similarity('', 'x'), 0);
    assert.equal(similarity('x', ''), 0);
});

test('buildRows: one changed word marks only that word', () => {
    const oldStr = 'the only reason that note can be trusted\ncoda\n';
    const newStr = 'the only reason the note can be trusted\ncoda\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const del = rows.find((r) => r.kind === 'del');
    const ins = rows.find((r) => r.kind === 'ins');

    assert.ok(del && ins);
    assert.equal(marked(del.segs), 'the only reason «that» note can be trusted');
    assert.equal(marked(ins.segs), 'the only reason «the» note can be trusted');
});

test('buildRows: unrelated lines are not word-diffed', () => {
    const oldStr = 'alpha beta gamma\n';
    const newStr = 'nothing in common here\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const del = rows.find((r) => r.kind === 'del');
    const ins = rows.find((r) => r.kind === 'ins');

    // A single unmarked segment: with no similarity the word-diff would say
    // "everything changed", which tells nothing.
    assert.deepEqual(del?.segs, [{ text: 'alpha beta gamma', changed: false }]);
    assert.deepEqual(ins?.segs, [{ text: 'nothing in common here', changed: false }]);
});

test('buildRows: a block of 2 removed -> 1 added does not pair at random', () => {
    const oldStr = 'first line same\nsecond line different\nctx\n';
    const newStr = 'first line same edited\nctx\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const dels = rows.filter((r) => r.kind === 'del');

    assert.equal(dels.length, 2);
    // The second '-' has no '+' to pair with: it stays whole.
    assert.deepEqual(dels[1].segs, [{ text: 'second line different', changed: false }]);
});

test('buildRows: line numbers advance on the right sides', () => {
    const oldStr = 'a\nb\nc\n';
    const newStr = 'a\nB\nc\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const del = rows.find((r) => r.kind === 'del');
    const ins = rows.find((r) => r.kind === 'ins');
    const ctx = rows.filter((r) => r.kind === 'ctx');

    assert.equal(del?.oldNo, 2);
    assert.equal(del?.newNo, null);
    assert.equal(ins?.oldNo, null);
    assert.equal(ins?.newNo, 2);
    assert.equal(ctx[0]?.oldNo, 1);
    assert.equal(ctx[0]?.newNo, 1);
});

test('editRangeFromHunks: the added lines, not the whole hunk', () => {
    // 3 context lines around one addition: the jump must point at the '+',
    // not at the whole hunk, or the editor would select untouched lines.
    const range = editRangeFromHunks([
        {
            oldStart: 46,
            oldLines: 4,
            newStart: 46,
            newLines: 5,
            lines: ['     a', '     b', '+        added', '     c', '     d'],
        },
    ]);

    assert.deepEqual(range, [48, 48]);
});

test('editRangeFromHunks: removed lines do not advance the counter', () => {
    const range = editRangeFromHunks([
        {
            oldStart: 10,
            oldLines: 4,
            newStart: 10,
            newLines: 3,
            lines: ['     a', '-        gone', '+        first', '+        second', '     b'],
        },
    ]);

    // 'a' is 10, the '-' does not count, the two '+' are 11 and 12
    assert.deepEqual(range, [11, 12]);
});

test('editRangeFromHunks: a context-only hunk falls back to its own range', () => {
    const range = editRangeFromHunks([
        {
            oldStart: 7,
            oldLines: 3,
            newStart: 7,
            newLines: 3,
            lines: ['     a', '     b', '     c'],
        },
    ]);

    assert.deepEqual(range, [7, 9]);
});

test('editRangeFromHunks: with no hunks there is nowhere to jump', () => {
    assert.deepEqual(editRangeFromHunks([]), [0, 0]);
    assert.deepEqual(editRangeFromHunks(null), [0, 0]);
});

test('editRangeFromHunks: only the first hunk decides the jump', () => {
    const range = editRangeFromHunks([
        { oldStart: 5, oldLines: 1, newStart: 5, newLines: 1, lines: ['+        first'] },
        { oldStart: 99, oldLines: 1, newStart: 99, newLines: 1, lines: ['+        second'] },
    ]);

    assert.deepEqual(range, [5, 5]);
});

test('rowsFromHunks: the numbers come from the CLI, not recounted from 1', () => {
    const rows = rowsFromHunks([
        {
            oldStart: 46,
            oldLines: 4,
            newStart: 46,
            newLines: 3,
            lines: ['     context', '-        return;', '     }', '     tail'],
        },
    ]);

    const del = rows.find((r) => r.kind === 'del');
    const ctx = rows.filter((r) => r.kind === 'ctx');

    assert.equal(ctx[0]?.oldNo, 46);
    assert.equal(ctx[0]?.newNo, 46);
    assert.equal(del?.oldNo, 47);
    assert.equal(del?.newNo, null);
    // after a removed line the two sides diverge: old advances, new does not
    assert.equal(ctx[1]?.oldNo, 48);
    assert.equal(ctx[1]?.newNo, 47);
});

test('rowsFromHunks: several hunks stay separated by their markers', () => {
    const rows = rowsFromHunks([
        { oldStart: 10, oldLines: 1, newStart: 10, newLines: 1, lines: ['-a', '+A'] },
        { oldStart: 90, oldLines: 1, newStart: 90, newLines: 1, lines: ['-b', '+B'] },
    ]);

    const hunks = rows.filter((r) => r.kind === 'hunk');
    assert.equal(hunks.length, 2);
    assert.equal(hunks[0].oldNo, 10);
    assert.equal(hunks[1].oldNo, 90);
});

test('rowsFromHunks: no hunks produces no rows', () => {
    assert.deepEqual(rowsFromHunks([]), []);
    assert.deepEqual(rowsFromHunks(null), []);
});

test('rowsFromHunks: the intra-line word-diff applies to CLI hunks too', () => {
    const rows = rowsFromHunks([
        {
            oldStart: 5,
            oldLines: 1,
            newStart: 5,
            newLines: 1,
            lines: ['-the only reason that note', '+the only reason the note'],
        },
    ]);

    const del = rows.find((r) => r.kind === 'del');
    assert.ok(del?.segs.some((s) => s.changed && s.text.includes('that')));
});

// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { clampPatch } from '../core/patch-clamp.ts';

/** Rows diff2html would build: the hunks, `@@` headers included, minus the trailing newline. */
function hunkLines(patch: string): number {
    const lines = patch.split('\n');
    const first = lines.findIndex((l) => l.startsWith('@@'));
    return first < 0 ? 0 : lines.length - first - 1;
}

/** A unified patch with `hunks` hunks of `perHunk` body lines each, in jsdiff's shape. */
function patchOf(hunks: number, perHunk: number): string {
    const out = ['Index: f.ts', '='.repeat(67), '--- f.ts', '+++ f.ts'];
    for (let h = 0; h < hunks; h++) {
        const at = h * 100 + 1;
        out.push(`@@ -${at},${perHunk} +${at},${perHunk} @@`);
        for (let i = 0; i < perHunk; i++) {
            out.push(i === 0 ? `-old ${h}.${i}` : ` ctx ${h}.${i}`);
        }
    }
    return `${out.join('\n')}\n`;
}

test('clampPatch: un patch che ci sta torna identico', () => {
    const patch = patchOf(1, 5);

    // Same reference, not just same text: a short diff must not pay a copy.
    assert.equal(clampPatch(patch, 60), patch);
});

test('clampPatch: taglia i hunk oltre il cap', () => {
    // Scattered edits: many hunks, which is what makes a patch long even at low context.
    const full = patchOf(20, 7);
    const clamped = clampPatch(full, 60);

    assert.ok(hunkLines(full) > 60, 'il patch di partenza deve superare il cap');
    assert.ok(clamped.length < full.length, 'il patch deve essere accorciato');
    assert.ok(hunkLines(clamped) <= 60, `corpo troppo lungo: ${hunkLines(clamped)}`);
});

test('clampPatch: taglia a confine di hunk, non a meta', () => {
    // 8 rows per hunk (@@ + 7), cap 20 → 2 whole hunks = 16; a third would reach 24.
    const clamped = clampPatch(patchOf(20, 7), 20);
    const hunks = clamped.split('\n').filter((l) => l.startsWith('@@')).length;

    assert.equal(hunks, 2, 'deve tenere i hunk interi che ci stanno');
    assert.equal(hunkLines(clamped), 16);
});

test('clampPatch: tiene l header del file, non solo le prime righe', () => {
    const clamped = clampPatch(patchOf(20, 7), 10);

    // Without the header diff2html has no file name and renders nothing usable.
    assert.match(clamped, /^Index: /m);
    assert.match(clamped, /^--- /m);
    assert.match(clamped, /^\+\+\+ /m);
    assert.match(clamped, /^@@ /m);
});

test('clampPatch: un solo hunk piu lungo del cap viene comunque tagliato', () => {
    // One contiguous block of changes: there is no hunk boundary to cut at.
    const clamped = clampPatch(patchOf(1, 200), 40);

    assert.equal(hunkLines(clamped), 40, 'deve fermarsi esattamente al cap');
});

test('clampPatch: un patch senza hunk (nessuna modifica) resta intatto', () => {
    const patch = 'Index: f.ts\n===\n--- f.ts\n+++ f.ts\n';

    assert.equal(clampPatch(patch, 10), patch);
});

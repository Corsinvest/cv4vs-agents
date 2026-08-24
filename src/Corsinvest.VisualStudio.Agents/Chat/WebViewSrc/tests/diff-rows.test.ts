// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildRows, similarity, editRangeFromHunks, rowsFromHunks } from '../core/diff-rows.ts';

/** Marked text of a row, "«x»" around the changed segments — readable in an assert. */
function marked(segs: { text: string; changed: boolean }[]): string {
    return segs.map((s) => (s.changed ? `«${s.text}»` : s.text)).join('');
}

test('similarity: righe identiche = 1, righe estranee = 0', () => {
    assert.equal(similarity('const x = 1', 'const x = 1'), 1);
    assert.equal(similarity('aaa bbb', 'xxx yyy'), 0);
});

test('similarity: ignora le righe vuote invece di dividere per zero', () => {
    assert.equal(similarity('', 'x'), 0);
    assert.equal(similarity('x', ''), 0);
});

test('buildRows: una parola cambiata marca solo quella', () => {
    const oldStr = 'the only reason that note can be trusted\ncoda\n';
    const newStr = 'the only reason the note can be trusted\ncoda\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const del = rows.find((r) => r.kind === 'del');
    const ins = rows.find((r) => r.kind === 'ins');

    assert.ok(del && ins);
    assert.equal(marked(del.segs), 'the only reason «that» note can be trusted');
    assert.equal(marked(ins.segs), 'the only reason «the» note can be trusted');
});

test('buildRows: righe estranee non vengono word-diffate', () => {
    const oldStr = 'alpha beta gamma\n';
    const newStr = 'nulla in comune qui\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const del = rows.find((r) => r.kind === 'del');
    const ins = rows.find((r) => r.kind === 'ins');

    // Un solo segmento non marcato: senza somiglianza il word-diff direbbe
    // "tutto cambiato", che non informa.
    assert.deepEqual(del?.segs, [{ text: 'alpha beta gamma', changed: false }]);
    assert.deepEqual(ins?.segs, [{ text: 'nulla in comune qui', changed: false }]);
});

test('buildRows: blocco 2 rimosse -> 1 aggiunta non accoppia a caso', () => {
    const oldStr = 'prima riga uguale\nseconda riga diversa\nctx\n';
    const newStr = 'prima riga uguale modificata\nctx\n';

    const rows = buildRows(oldStr, newStr, 'f.cs', 3, false);
    const dels = rows.filter((r) => r.kind === 'del');

    assert.equal(dels.length, 2);
    // La seconda '-' non ha una '+' con cui accoppiarsi: resta intera.
    assert.deepEqual(dels[1].segs, [{ text: 'seconda riga diversa', changed: false }]);
});

test('buildRows: i numeri di riga avanzano sui lati giusti', () => {
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

test('editRangeFromHunks: le righe aggiunte, non tutto lo hunk', () => {
    // 3 righe di contesto attorno a una aggiunta: il salto deve puntare alla '+',
    // non allo hunk intero, o l'editor selezionerebbe righe non toccate.
    const range = editRangeFromHunks([
        {
            oldStart: 46,
            oldLines: 4,
            newStart: 46,
            newLines: 5,
            lines: ['     a', '     b', '+        nuova', '     c', '     d'],
        },
    ]);

    assert.deepEqual(range, [48, 48]);
});

test('editRangeFromHunks: le righe tolte non fanno avanzare il contatore', () => {
    const range = editRangeFromHunks([
        {
            oldStart: 10,
            oldLines: 4,
            newStart: 10,
            newLines: 3,
            lines: ['     a', '-        via', '+        prima', '+        seconda', '     b'],
        },
    ]);

    // 'a' e' 10, la '-' non conta, le due '+' sono 11 e 12
    assert.deepEqual(range, [11, 12]);
});

test('editRangeFromHunks: uno hunk di solo contesto ricade sul suo intervallo', () => {
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

test('editRangeFromHunks: senza hunk non c e nessun punto dove saltare', () => {
    assert.deepEqual(editRangeFromHunks([]), [0, 0]);
    assert.deepEqual(editRangeFromHunks(null), [0, 0]);
});

test('editRangeFromHunks: solo il primo hunk decide il salto', () => {
    const range = editRangeFromHunks([
        { oldStart: 5, oldLines: 1, newStart: 5, newLines: 1, lines: ['+        prima'] },
        { oldStart: 99, oldLines: 1, newStart: 99, newLines: 1, lines: ['+        seconda'] },
    ]);

    assert.deepEqual(range, [5, 5]);
});

test('rowsFromHunks: i numeri sono quelli del CLI, non ricontati da 1', () => {
    const rows = rowsFromHunks([
        {
            oldStart: 46,
            oldLines: 4,
            newStart: 46,
            newLines: 3,
            lines: ['     contesto', '-        return;', '     }', '     coda'],
        },
    ]);

    const del = rows.find((r) => r.kind === 'del');
    const ctx = rows.filter((r) => r.kind === 'ctx');

    assert.equal(ctx[0]?.oldNo, 46);
    assert.equal(ctx[0]?.newNo, 46);
    assert.equal(del?.oldNo, 47);
    assert.equal(del?.newNo, null);
    // dopo una riga tolta i due lati divergono: old avanza, new no
    assert.equal(ctx[1]?.oldNo, 48);
    assert.equal(ctx[1]?.newNo, 47);
});

test('rowsFromHunks: piu hunk restano separati dai loro marcatori', () => {
    const rows = rowsFromHunks([
        { oldStart: 10, oldLines: 1, newStart: 10, newLines: 1, lines: ['-a', '+A'] },
        { oldStart: 90, oldLines: 1, newStart: 90, newLines: 1, lines: ['-b', '+B'] },
    ]);

    const hunks = rows.filter((r) => r.kind === 'hunk');
    assert.equal(hunks.length, 2);
    assert.equal(hunks[0].oldNo, 10);
    assert.equal(hunks[1].oldNo, 90);
});

test('rowsFromHunks: nessun hunk non produce righe', () => {
    assert.deepEqual(rowsFromHunks([]), []);
    assert.deepEqual(rowsFromHunks(null), []);
});

test('rowsFromHunks: il word-diff intra-riga vale anche sugli hunk del CLI', () => {
    const rows = rowsFromHunks([
        {
            oldStart: 5,
            oldLines: 1,
            newStart: 5,
            newLines: 1,
            lines: ['-la sola ragione che quella nota', '+la sola ragione che la nota'],
        },
    ]);

    const del = rows.find((r) => r.kind === 'del');
    assert.ok(del?.segs.some((s) => s.changed && s.text.includes('quella')));
});

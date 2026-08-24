// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildRows, similarity } from '../core/diff-rows.ts';

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

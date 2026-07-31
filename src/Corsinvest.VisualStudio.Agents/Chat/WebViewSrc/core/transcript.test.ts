// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Transcript } from './transcript.ts';
import type { UiUserEntry } from './types';

function userEntry(id: number, text = 't'): UiUserEntry {
    return { kind: 'text', id, role: 'user', text };
}

test('append: nuovo array, entry esistenti mantengono il riferimento', () => {
    const t = new Transcript();
    const a = userEntry(1);
    t.append(a);
    const first = t.entries;

    t.append(userEntry(2));

    assert.notEqual(t.entries, first, 'entries deve essere un array nuovo');
    assert.equal(t.entries[0], a, 'la entry esistente mantiene il riferimento');
    assert.equal(t.entries.length, 2);
});

test('find: ritorna la entry per id, null se assente', () => {
    const t = new Transcript();
    const a = userEntry(7);
    t.append(a);

    assert.equal(t.find(7), a);
    assert.equal(t.find(99), null);
});

test('clear: svuota entries e index', () => {
    const t = new Transcript();
    t.append(userEntry(1));

    t.clear();

    assert.equal(t.entries.length, 0);
    assert.equal(t.find(1), null, 'index deve essere svuotato con le entries');
});

test('update: sostituisce la entry, gli altri rami restano invariati', () => {
    const t = new Transcript();
    const a = userEntry(1, 'a');
    const b = userEntry(2, 'b');
    t.append(a);
    t.append(b);

    const ok = t.update<UiUserEntry>(1, (e) => ({ ...e, text: 'nuovo' }));

    assert.equal(ok, true);
    assert.notEqual(t.entries[0], a, 'la entry aggiornata ha un riferimento nuovo');
    assert.equal((t.entries[0] as UiUserEntry).text, 'nuovo');
    assert.equal(t.entries[1], b, 'i rami non toccati mantengono il riferimento');
});

test('update: id assente ritorna false senza lanciare', () => {
    const t = new Transcript();

    assert.equal(
        t.update(42, (e) => e),
        false,
    );
});

test('appendText: concatena e ricrea solo quella entry', () => {
    const t = new Transcript();
    const a = userEntry(1, 'ab');
    const b = userEntry(2, 'x');
    t.append(a);
    t.append(b);

    assert.equal(t.appendText(1, 'cd'), true);
    assert.equal((t.entries[0] as UiUserEntry).text, 'abcd');
    assert.equal(t.entries[1], b, 'gli altri rami restano invariati');
});

test('appendText: id assente ritorna false', () => {
    const t = new Transcript();

    assert.equal(t.appendText(9, 'x'), false);
});

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

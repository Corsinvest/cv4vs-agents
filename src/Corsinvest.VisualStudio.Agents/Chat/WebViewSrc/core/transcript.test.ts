// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Transcript } from './transcript.ts';
import type { UiEntry, UiToolEntry, UiUserEntry } from './types';

function userEntry(id: number, text = 't'): UiUserEntry {
    return { kind: 'text', id, role: 'user', text };
}

function toolEntry(id: number, toolUseId: string): UiToolEntry {
    return {
        kind: 'tool',
        id,
        toolUseId,
        data: { id: toolUseId, name: 'Agent', input: {} },
        status: 'pending',
        result: '',
        fullLineCount: 0,
        editStartLine: 0,
        editEndLine: 0,
        elapsedSec: 0,
    };
}

/** What cv-app uses to decide two children are the same row. */
const childKey = (e: UiEntry): string => (e.kind === 'tool' ? e.toolUseId : `t${e.id}`);

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

test('appendChild: parent, children e items nuovi; fratelli invariati', () => {
    const t = new Transcript();
    const parent = toolEntry(1, 'tool-1');
    const sibling = userEntry(2);
    t.append(parent);
    t.append(sibling);

    const ok = t.appendChild('tool-1', userEntry(3), childKey);

    assert.equal(ok, true);
    const p = t.entries[0] as UiToolEntry;
    assert.notEqual(p, parent, 'il parent ha un riferimento nuovo');
    assert.notEqual(p.children, parent.children, 'anche il blocco children');
    assert.equal(p.children?.items.length, 1);
    assert.equal(t.entries[1], sibling, 'il fratello mantiene il riferimento');
});

// Il difetto del 2026-07-31: il ring scartava un figlio mutando items in place, e il blocco
// children del parent restava identico per ===. cv-tool-row non veniva aggiornato e i suoi
// ChildPart puntavano a nodi rimossi.
test('appendChild: il ring che scarta un figlio ricrea comunque il blocco children', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    for (let i = 2; i <= 4; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }
    const before = (t.entries[0] as UiToolEntry).children;

    t.appendChild('tool-1', userEntry(5), childKey);

    const after = (t.entries[0] as UiToolEntry).children;
    assert.notEqual(after, before, 'il blocco children deve avere identita nuova');
    assert.notEqual(after?.items, before?.items, 'e cosi items');
    assert.equal(after?.items.length, 3, 'restano gli ultimi 3');
    assert.equal(after?.hasMore, true);
    assert.deepEqual(
        after?.items.map((e) => e.id),
        [3, 4, 5],
    );
});

test('appendChild: showAll tiene tutto e fa upsert invece di duplicare', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    t.update<UiToolEntry>(1, (e) => ({
        ...e,
        children: { items: [], hasMore: false, showAll: true },
    }));
    for (let i = 2; i <= 5; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }

    // re-emissione dello stesso figlio: aggiorna in place, non duplica
    t.appendChild('tool-1', userEntry(3, 'aggiornato'), childKey);

    const p = t.entries[0] as UiToolEntry;
    assert.equal(p.children?.items.length, 4, 'showAll tiene tutto, upsert non duplica');
    assert.equal((p.children?.items[1] as UiUserEntry).text, 'aggiornato');
});

test('appendChild: parent inesistente ritorna false', () => {
    const t = new Transcript();

    assert.equal(t.appendChild('nope', userEntry(1), childKey), false);
});

test('appendChild annidato: l index risolve un figlio di figlio', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'outer'));
    t.appendChild('outer', toolEntry(2, 'inner'), childKey);
    t.appendChild('inner', userEntry(3, 'profondo'), childKey);

    assert.equal(t.find(3)?.id, 3);
    assert.equal(t.findTool('inner')?.id, 2);
    assert.equal(
        t.update<UiUserEntry>(3, (e) => ({ ...e, text: 'x' })),
        true,
        'update deve raggiungere un figlio di secondo livello',
    );
});

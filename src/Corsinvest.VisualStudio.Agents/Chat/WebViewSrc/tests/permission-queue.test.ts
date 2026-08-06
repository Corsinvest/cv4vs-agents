// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PermissionQueue } from '../core/permission-queue.ts';

const req = (id: string): { id: string } => ({ id });

test('push: la prima va a schermo, la seconda aspetta', () => {
    const q = new PermissionQueue();

    assert.equal(q.push(req('a')), true, 'niente a schermo -> va subito');
    assert.equal(q.push(req('b')), false, 'una gia aperta -> aspetta');

    assert.equal(q.current?.id, 'a');
    assert.deepEqual(
        q.waiting.map((r) => r.id),
        ['b'],
    );
});

test('push: la richiesta gia nota non entra due volte', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    // Il CLI rimanda un can_use_tool quando il turno viene rigiocato: rispondere
    // due volte allo stesso tool_use_id e' peggio che rispondere una volta sola.
    assert.equal(q.push(req('a')), false, 'duplicato di quella a schermo');
    assert.equal(q.push(req('b')), false, 'duplicato di una in attesa');
    assert.equal(q.size, 2);
});

test('next: promuove in ordine di arrivo, poi svuota', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));
    q.push(req('c'));

    assert.equal(q.next()?.id, 'b');
    assert.equal(q.next()?.id, 'c');
    assert.equal(q.next(), null, 'coda finita');
    assert.equal(q.size, 0);
});

test('drop: toglie quella a schermo e promuove la successiva', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    assert.equal(q.drop('a')?.id, 'b', 'ritorna chi e finito a schermo');
    assert.equal(q.current?.id, 'b');
});

test('drop: toglie una in attesa senza toccare quella a schermo', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));
    q.push(req('c'));

    // Il CLI annulla per id una richiesta superata: se restasse in coda,
    // spunterebbe dopo chiedendo di un tool che ha gia girato.
    assert.equal(q.drop('b')?.id, 'a', 'quella a schermo non cambia');
    assert.deepEqual(
        q.waiting.map((r) => r.id),
        ['c'],
    );
});

test('drop: id sconosciuto non tocca nulla', () => {
    const q = new PermissionQueue();
    q.push(req('a'));

    assert.equal(q.drop('mai-vista')?.id, 'a');
    assert.equal(q.size, 1);
});

test('drop: l ultima lascia la coda vuota', () => {
    const q = new PermissionQueue();
    q.push(req('a'));

    assert.equal(q.drop('a'), null);
    assert.equal(q.current, null);
});

test('clear: butta tutto, anche chi aspettava', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    q.clear();

    assert.equal(q.current, null);
    assert.equal(q.size, 0);
});

test('dopo clear la coda riparte pulita', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.clear();

    // Stesso id di prima: dopo un cambio sessione non e' piu un duplicato.
    assert.equal(q.push(req('a')), true);
    assert.equal(q.current?.id, 'a');
});

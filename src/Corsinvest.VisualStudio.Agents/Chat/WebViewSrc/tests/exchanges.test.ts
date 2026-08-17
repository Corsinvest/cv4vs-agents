// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildGroups } from '../core/exchanges.ts';
import type { UiAssistantEntry, UiUserEntry } from '../core/types';

const user = (id: number, uuid?: string): UiUserEntry => ({
    kind: 'text',
    id,
    role: 'user',
    text: `u${id}`,
    uuid,
});
const bot = (id: number): UiAssistantEntry => ({
    kind: 'text',
    id,
    role: 'assistant',
    text: `a${id}`,
});

test('ogni messaggio utente apre un nuovo scambio', () => {
    const groups = buildGroups([user(1), bot(2), user(3), bot(4)]);

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [3, 4],
    );
});

test('le entry prima del primo utente vanno in un gruppo di testa', () => {
    const groups = buildGroups([bot(1), user(2), bot(3)]);

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [2, 3],
    );
});

test('lista vuota produce zero gruppi', () => {
    assert.deepEqual(buildGroups([]), []);
});

test('un messaggio ancora in coda non apre uno scambio', () => {
    // Il turno 1 sta ancora rispondendo (bot(4) arriva dopo) quando l utente scrive il secondo
    // prompt: finche resta in coda deve stare nel gruppo del turno che gira, o la sua <section>
    // finirebbe li e l intestazione del turno in corso si sbloccherebbe a meta risposta.
    const groups = buildGroups([user(1), bot(2), user(3, 'q'), bot(4)], new Set(['q']));

    assert.equal(groups.length, 1);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2, 3, 4],
    );
});

test('lo stesso messaggio apre lo scambio appena esce dalla coda', () => {
    const entries = [user(1), bot(2), user(3, 'q')];

    assert.equal(buildGroups(entries, new Set(['q'])).length, 1);
    assert.equal(buildGroups(entries, new Set()).length, 2);
});

test('la coda trattiene solo il suo uuid, non ogni utente', () => {
    // 'b' e in coda, 'a' no: 'a' apre il suo scambio, 'b' resta nel gruppo che trova.
    const groups = buildGroups([user(1, 'z'), user(2, 'b'), user(3, 'a')], new Set(['b']));

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [3],
    );
});

// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildGroups } from './exchanges.ts';
import type { UiAssistantEntry, UiUserEntry } from './types';

const user = (id: number): UiUserEntry => ({ kind: 'text', id, role: 'user', text: `u${id}` });
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

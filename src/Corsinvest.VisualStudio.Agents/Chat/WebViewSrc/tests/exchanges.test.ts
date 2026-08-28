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

test('every user message opens a new exchange', () => {
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

test('entries before the first user go into a leading group', () => {
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

test('an empty list produces zero groups', () => {
    assert.deepEqual(buildGroups([]), []);
});

test('a message still queued does not open an exchange', () => {
    // Turn 1 is still answering (bot(4) arrives later) when the user types the second prompt:
    // while it stays queued it must live in the running turn's group, or its <section> would end
    // there and the running turn's header would unstick halfway through the answer.
    const groups = buildGroups([user(1), bot(2), user(3, 'q'), bot(4)], new Set(['q']));

    assert.equal(groups.length, 1);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2, 3, 4],
    );
});

test('the same message opens the exchange as soon as it leaves the queue', () => {
    const entries = [user(1), bot(2), user(3, 'q')];

    assert.equal(buildGroups(entries, new Set(['q'])).length, 1);
    assert.equal(buildGroups(entries, new Set()).length, 2);
});

test('the queue holds back only its own uuid, not every user message', () => {
    // 'b' is queued, 'a' is not: 'a' opens its own exchange, 'b' stays in the group it finds.
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

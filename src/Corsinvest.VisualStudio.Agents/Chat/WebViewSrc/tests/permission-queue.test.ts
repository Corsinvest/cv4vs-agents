// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PermissionQueue } from '../core/permission-queue.ts';

const req = (id: string): { id: string } => ({ id });

test('push: the first one goes on screen, the second waits', () => {
    const q = new PermissionQueue();

    assert.equal(q.push(req('a')), true, 'nothing on screen -> shows at once');
    assert.equal(q.push(req('b')), false, 'one already open -> waits');

    assert.equal(q.current?.id, 'a');
    assert.deepEqual(
        q.waiting.map((r) => r.id),
        ['b'],
    );
});

test('push: an already known request never enters twice', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    // The CLI resends a can_use_tool when the turn is replayed: answering the same tool_use_id
    // twice is worse than answering it once.
    assert.equal(q.push(req('a')), false, 'duplicate of the one on screen');
    assert.equal(q.push(req('b')), false, 'duplicate of a waiting one');
    assert.equal(q.size, 2);
});

test('next: promotes in arrival order, then empties', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));
    q.push(req('c'));

    assert.equal(q.next()?.id, 'b');
    assert.equal(q.next()?.id, 'c');
    assert.equal(q.next(), null, 'queue exhausted');
    assert.equal(q.size, 0);
});

test('drop: removes the one on screen and promotes the next', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    assert.equal(q.drop('a')?.id, 'b', 'returns whoever ended up on screen');
    assert.equal(q.current?.id, 'b');
});

test('drop: removes a waiting one without touching the one on screen', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));
    q.push(req('c'));

    // The CLI cancels a superseded request by id: left in the queue it would pop up later
    // asking about a tool that has already run.
    assert.equal(q.drop('b')?.id, 'a', 'the one on screen does not change');
    assert.deepEqual(
        q.waiting.map((r) => r.id),
        ['c'],
    );
});

test('drop: an unknown id touches nothing', () => {
    const q = new PermissionQueue();
    q.push(req('a'));

    assert.equal(q.drop('never-seen')?.id, 'a');
    assert.equal(q.size, 1);
});

test('drop: the last one leaves the queue empty', () => {
    const q = new PermissionQueue();
    q.push(req('a'));

    assert.equal(q.drop('a'), null);
    assert.equal(q.current, null);
});

test('clear: throws everything away, the waiting ones included', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.push(req('b'));

    q.clear();

    assert.equal(q.current, null);
    assert.equal(q.size, 0);
});

test('after clear the queue restarts clean', () => {
    const q = new PermissionQueue();
    q.push(req('a'));
    q.clear();

    // Same id as before: after a session change it is no longer a duplicate.
    assert.equal(q.push(req('a')), true);
    assert.equal(q.current?.id, 'a');
});

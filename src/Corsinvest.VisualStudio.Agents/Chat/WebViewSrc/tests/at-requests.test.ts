/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AtRequests } from '../core/at-requests';

test('the first request of an opening asks for a new listing, later ones do not', () => {
    const r = new AtRequests();
    assert.equal(r.next(true)?.refresh, true);
    assert.equal(r.next(true)?.refresh, false);
});

test('closing makes the next opening list again', () => {
    const r = new AtRequests();
    r.next(true);
    r.closed();
    assert.equal(r.next(true)?.refresh, true);
});

test('a request fired after the picker closed is not sent and does not use up the refresh', () => {
    // Typing `@Foo` and pressing Enter within the debounce: the timer fires with the picker shut.
    const r = new AtRequests();
    r.next(true);
    r.closed();
    assert.equal(r.next(false), null);
    assert.equal(r.next(true)?.refresh, true);
});

test('only the latest request is current', () => {
    const r = new AtRequests();
    const first = r.next(true)!;
    const second = r.next(true)!;
    assert.equal(r.isLatest(first.seq), false);
    assert.equal(r.isLatest(second.seq), true);
});

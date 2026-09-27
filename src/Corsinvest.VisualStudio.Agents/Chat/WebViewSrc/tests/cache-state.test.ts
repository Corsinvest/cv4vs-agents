// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cacheState, msUntilCacheChange } from '../core/ai-models.ts';
import type { ContextUsageDto } from '../core/types.ts';

const MIN = 60_000;
const T0 = 1_000_000_000_000;

function usage(cacheTtl: string): ContextUsageDto {
    return {
        inputTokens: 10,
        outputTokens: 5,
        cacheReadTokens: 900,
        cacheCreationTokens: 90,
        cacheTtl,
    };
}

test('cacheState: 1h TTL is warm until five minutes are left, then expiring', () => {
    assert.deepEqual(cacheState(usage('1h'), T0, T0 + 50 * MIN, null), {
        kind: 'warm',
        minutesLeft: 10,
    });
    assert.deepEqual(cacheState(usage('1h'), T0, T0 + 55 * MIN, null), {
        kind: 'expiring',
        minutesLeft: 5,
    });
    assert.deepEqual(cacheState(usage('1h'), T0, T0 + 59.5 * MIN, null), {
        kind: 'expiring',
        minutesLeft: 1,
    });
});

test('cacheState: 5m TTL is expiring only in its last minute, not from the start', () => {
    assert.equal(cacheState(usage('5m'), T0, T0 + 1 * MIN, null).kind, 'warm');
    assert.equal(cacheState(usage('5m'), T0, T0 + 4 * MIN, null).kind, 'expiring');
});

test('cacheState: past the TTL it is cold, with what the next message re-caches', () => {
    assert.deepEqual(cacheState(usage('1h'), T0, T0 + 90 * MIN, null), {
        kind: 'cold',
        reason: 'expired',
        idleMs: 90 * MIN,
        recacheTokens: 1000,
    });
});

test('cacheState: a compaction no reply has cached is cold, whatever the clock says', () => {
    assert.deepEqual(cacheState(usage('1h'), T0, T0 + 1 * MIN, T0), {
        kind: 'cold',
        reason: 'compacted',
    });
});

test('cacheState: no usage, no anchor or an unknown TTL is unknown', () => {
    assert.equal(cacheState(null, T0, T0, null).kind, 'unknown');
    assert.equal(cacheState(usage('1h'), null, T0, null).kind, 'unknown');
    assert.equal(cacheState(usage(''), T0, T0, null).kind, 'unknown');
});

test('msUntilCacheChange: warm aims at the threshold, expiring at each minute, cold at nothing', () => {
    // Warm, 10 min left: next change when 5 are left.
    assert.equal(msUntilCacheChange(usage('1h'), T0, T0 + 50 * MIN, null), 5 * MIN);
    // Expiring, 4.5 min left: the shown count drops to 4 in 30 s.
    assert.equal(msUntilCacheChange(usage('1h'), T0, T0 + 55.5 * MIN, null), 0.5 * MIN);
    // Exactly on a minute: the next drop is a full minute away.
    assert.equal(msUntilCacheChange(usage('1h'), T0, T0 + 56 * MIN, null), MIN);
    assert.equal(msUntilCacheChange(usage('1h'), T0, T0 + 61 * MIN, null), null);
    assert.equal(msUntilCacheChange(usage('1h'), T0, T0 + 1 * MIN, T0), null);
});

test('msUntilCacheChange: aimed at a change, the reading on the far side has changed', () => {
    for (const now of [T0 + 10 * MIN, T0 + 55.2 * MIN, T0 + 59.9 * MIN]) {
        const ms = msUntilCacheChange(usage('1h'), T0, now, null);
        assert.ok(ms !== null);
        const before = cacheState(usage('1h'), T0, now, null);
        const after = cacheState(usage('1h'), T0, now + ms + 1, null);
        assert.notDeepEqual(after, before);
    }
});

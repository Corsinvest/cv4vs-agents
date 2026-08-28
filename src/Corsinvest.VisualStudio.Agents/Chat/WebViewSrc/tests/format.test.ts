// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    formatTimeAgo,
    formatDuration,
    formatDurationSec,
    formatTokenCount,
    formatTokens,
} from '../ui/helpers/format.ts';

const MINUTE = 60_000;
const HOUR = 3600_000;

test('formatTimeAgo: the clock is injectable, not a hard-wired Date.now', () => {
    const t = 1_000_000_000_000;

    // The same timestamp read at two different moments must give two different strings: that is
    // what makes the refresh on hover a matter of state, not of writing into the DOM.
    const justNow = formatTimeAgo(t, t + MINUTE);
    const later = formatTimeAgo(t, t + 3 * HOUR);

    assert.notEqual(justNow, later, 'a clock further ahead must give a different text');
});

// The exact text depends on the system locale (Intl.RelativeTimeFormat): asserting on English
// strings would make the test green only on English machines. What matters is that different
// units give different strings, and that the number is the right one.
test('formatTimeAgo: picks the unit from the distance', () => {
    const t = 1_000_000_000_000;

    const sec = formatTimeAgo(t, t + 30_000);
    const min = formatTimeAgo(t, t + 5 * MINUTE);
    const hours = formatTimeAgo(t, t + 5 * HOUR);

    assert.notEqual(sec, min, 'seconds and minutes must differ');
    assert.notEqual(min, hours, 'minutes and hours must differ');
    assert.match(min, /5/, 'the number must show up');
    assert.match(hours, /5/);
});

test('formatTimeAgo: with no explicit clock it uses now', () => {
    // The default must stay Date.now(): callers that pass no clock are unaffected.
    const explicitClock = formatTimeAgo(1_000_000_000_000, 1_000_000_000_000 + 2 * MINUTE);
    const implicitClock = formatTimeAgo(Date.now() - 2 * MINUTE);

    assert.equal(
        implicitClock,
        explicitClock,
        'the default must be the same as passing Date.now()',
    );
});

// formatDuration is the same function for the turn spinner, the answer row, the thinking, the
// sub-agents and the tool rows: a change here changes all five at once.

test('formatDuration: the decimal shows only below one second', () => {
    // A 200ms turn must not read "0s" (it would look like a bug), but a counter ticking through
    // seconds must not show ".0" on every tick.
    assert.equal(formatDuration(200), '0.2s');
    assert.equal(formatDuration(1000), '1s');
    assert.equal(formatDuration(3200), '3s');
    assert.equal(formatDuration(45_000), '45s');
});

test('formatDuration: past the minute it switches to "Nm Ns"', () => {
    assert.equal(formatDuration(60_000), '1m 0s');
    assert.equal(formatDuration(83_000), '1m 23s');
    // The boundary: 59s stays in seconds, 60 becomes minutes.
    assert.equal(formatDuration(59_000), '59s');
});

test('formatDurationSec: it is formatDuration in seconds, same rendering', () => {
    // The spinner and the tool rows hold seconds, not ms: the two signatures must not diverge.
    assert.equal(formatDurationSec(45), formatDuration(45_000));
    assert.equal(formatDurationSec(83), formatDuration(83_000));
});

test('formatTokens: compacts only past the thousand', () => {
    assert.equal(formatTokens(84), '84');
    assert.equal(formatTokens(1500), '2k');
    assert.equal(formatTokens(357_000), '357k');
    assert.equal(formatTokens(1_200_000), '1.2M');
});

test('formatTokenCount: carries the unit, and the tilde only when estimated', () => {
    // The tilde tells the measured answer count from the thinking estimate: the two numbers sit
    // next to each other in the same chat and without the sign they would look like the same thing.
    assert.equal(formatTokenCount(84), '84 tok');
    assert.equal(formatTokenCount(84, true), '~84 tok');
    assert.equal(formatTokenCount(1500, true), '~2k tok');
});

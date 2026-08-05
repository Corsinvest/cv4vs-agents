// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatTimeAgo } from '../core/time.ts';

const MINUTE = 60_000;
const HOUR = 3600_000;

test('formatTimeAgo: il clock e iniettabile, non e Date.now cablato', () => {
    const t = 1_000_000_000_000;

    // The same timestamp read at two different moments must give two different strings: that is
    // what makes the refresh on hover a matter of state, not of writing into the DOM.
    const subito = formatTimeAgo(t, t + MINUTE);
    const dopo = formatTimeAgo(t, t + 3 * HOUR);

    assert.notEqual(subito, dopo, 'un clock piu avanti deve dare un testo diverso');
});

// The exact text depends on the system locale (Intl.RelativeTimeFormat): asserting on English
// strings would make the test green only on English machines. What matters is that different
// units give different strings, and that the number is the right one.
test('formatTimeAgo: sceglie l unita in base alla distanza', () => {
    const t = 1_000_000_000_000;

    const sec = formatTimeAgo(t, t + 30_000);
    const min = formatTimeAgo(t, t + 5 * MINUTE);
    const ore = formatTimeAgo(t, t + 5 * HOUR);

    assert.notEqual(sec, min, 'secondi e minuti devono differire');
    assert.notEqual(min, ore, 'minuti e ore devono differire');
    assert.match(min, /5/, 'il numero deve comparire');
    assert.match(ore, /5/);
});

test('formatTimeAgo: senza clock esplicito usa adesso', () => {
    // The default must stay Date.now(): callers that pass no clock are unaffected.
    const esplicito = formatTimeAgo(1_000_000_000_000, 1_000_000_000_000 + 2 * MINUTE);
    const implicito = formatTimeAgo(Date.now() - 2 * MINUTE);

    assert.equal(implicito, esplicito, 'il default deve equivalere a passare Date.now()');
});

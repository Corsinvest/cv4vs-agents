// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatTimeAgo } from './time.ts';

const MINUTE = 60_000;
const HOUR = 3600_000;

test('formatTimeAgo: il clock e iniettabile, non e Date.now cablato', () => {
    const t = 1_000_000_000_000;

    // Lo stesso timestamp letto a due momenti diversi deve dare due stringhe diverse: e quello che
    // rende il refresh su hover una questione di stato, non di scrittura nel DOM.
    const subito = formatTimeAgo(t, t + MINUTE);
    const dopo = formatTimeAgo(t, t + 3 * HOUR);

    assert.notEqual(subito, dopo, 'un clock piu avanti deve dare un testo diverso');
});

// Il testo esatto dipende dal locale di sistema (Intl.RelativeTimeFormat): asserire su stringhe
// inglesi renderebbe il test verde solo sulle macchine in inglese. Quello che conta e' che unita
// diverse diano stringhe diverse, e che il numero sia quello giusto.
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
    // Il default deve restare Date.now(): i chiamanti che non passano il clock non cambiano.
    const esplicito = formatTimeAgo(1_000_000_000_000, 1_000_000_000_000 + 2 * MINUTE);
    const implicito = formatTimeAgo(Date.now() - 2 * MINUTE);

    assert.equal(implicito, esplicito, 'il default deve equivalere a passare Date.now()');
});

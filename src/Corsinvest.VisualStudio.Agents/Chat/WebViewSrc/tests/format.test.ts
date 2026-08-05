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

// formatDuration e la stessa funzione per lo spinner del turno, la riga di risposta, il thinking,
// i sub-agent e le tool row: se cambia qui, cambia in tutti e cinque insieme.

test('formatDuration: il decimale appare solo sotto il secondo', () => {
    // Un turno da 200ms non deve leggersi "0s" (sembrerebbe un errore), ma un contatore che
    // scandisce i secondi non deve mostrare ".0" a ogni tick.
    assert.equal(formatDuration(200), '0.2s');
    assert.equal(formatDuration(1000), '1s');
    assert.equal(formatDuration(3200), '3s');
    assert.equal(formatDuration(45_000), '45s');
});

test('formatDuration: oltre il minuto passa a "Nm Ns"', () => {
    assert.equal(formatDuration(60_000), '1m 0s');
    assert.equal(formatDuration(83_000), '1m 23s');
    // Il confine: 59s resta in secondi, 60 diventa minuti.
    assert.equal(formatDuration(59_000), '59s');
});

test('formatDurationSec: e formatDuration in secondi, stessa resa', () => {
    // Lo spinner e le tool row tengono secondi, non ms: le due firme non devono divergere.
    assert.equal(formatDurationSec(45), formatDuration(45_000));
    assert.equal(formatDurationSec(83), formatDuration(83_000));
});

test('formatTokens: compatta solo oltre il migliaio', () => {
    assert.equal(formatTokens(84), '84');
    assert.equal(formatTokens(1500), '2k');
    assert.equal(formatTokens(357_000), '357k');
    assert.equal(formatTokens(1_200_000), '1.2M');
});

test('formatTokenCount: porta l unita, e la tilde solo se stimato', () => {
    // La tilde distingue il conteggio misurato della risposta dalla stima del thinking: i due
    // numeri stanno vicini nella stessa chat e senza il segno sembrerebbero la stessa cosa.
    assert.equal(formatTokenCount(84), '84 tok');
    assert.equal(formatTokenCount(84, true), '~84 tok');
    assert.equal(formatTokenCount(1500, true), '~2k tok');
});

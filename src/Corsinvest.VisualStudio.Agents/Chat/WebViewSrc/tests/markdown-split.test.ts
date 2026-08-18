/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stableMarkdownSplit } from '../core/markdown-split.ts';

test('split: taglia dopo una riga vuota', () => {
    const t = 'primo paragrafo\n\nsecondo in corso';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'primo paragrafo\n\n');
});

test('split: non taglia dentro un code block aperto', () => {
    const t = 'testo\n\n```ts\nconst a = 1;\n\nconst b = 2;';
    const cut = stableMarkdownSplit(t);
    // Il solo punto sicuro e' la riga vuota PRIMA della fence.
    assert.equal(t.slice(0, cut), 'testo\n\n');
});

test('split: riprende dopo una fence chiusa', () => {
    const t = 'testo\n\n```ts\nconst a = 1;\n```\n\ncoda in corso';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'testo\n\n```ts\nconst a = 1;\n```\n\n');
});

test('split: la riga che chiude una fence e gia un punto di taglio', () => {
    // Senza riga vuota dopo la fence: il blocco e' comunque finito, e lasciarlo nella coda
    // significa ri-evidenziarlo per intero a ogni passata.
    const t = 'testo\n\n```ts\nconst a = 1;\n```\ncoda in corso';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'testo\n\n```ts\nconst a = 1;\n```\n');
});

test('split: una fence ancora aperta non e un punto di taglio', () => {
    const t = 'testo\n\n```ts\nconst a = 1;';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'testo\n\n');
});

test('split: senza righe vuote non taglia', () => {
    assert.equal(stableMarkdownSplit('un solo paragrafo che cresce'), 0);
});

test('split: testo vuoto', () => {
    assert.equal(stableMarkdownSplit(''), 0);
});

test('split: mai oltre la fine del testo', () => {
    const t = 'paragrafo\n\n';
    assert.equal(stableMarkdownSplit(t), 0);
});

test('split: una fence che contiene backtick non sfasa il conteggio', () => {
    const t = 'a\n\n```md\nesempio con ``` dentro\n```\n\nb in corso';
    const cut = stableMarkdownSplit(t);
    assert.ok(cut > 0);
    // Il taglio non cade mai a meta' del blocco: il prefisso ha fence bilanciate.
    const fences = (t.slice(0, cut).match(/^\s*```/gm) ?? []).length;
    assert.equal(fences % 2, 0);
});

test('split: il prefisso e sempre un confine di riga', () => {
    const t = 'uno\n\ndue\n\ntre in corso';
    const cut = stableMarkdownSplit(t);
    assert.ok(cut === 0 || t[cut - 1] === '\n');
});

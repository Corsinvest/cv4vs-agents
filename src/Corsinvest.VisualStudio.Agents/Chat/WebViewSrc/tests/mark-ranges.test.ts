// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { markRanges, rangesOf } from '../core/mark-ranges.ts';

test('markRanges: senza range restituisce l’html intatto', () => {
    assert.equal(
        markRanges('<span class="k">const</span> x', []),
        '<span class="k">const</span> x',
    );
});

test('markRanges: marca un tratto di testo semplice', () => {
    assert.equal(markRanges('abcdef', [{ start: 2, end: 4 }]), 'ab<mark>cd</mark>ef');
});

test('markRanges: gli offset sono quelli del testo, non dell’html', () => {
    // 'const x' con i primi cinque caratteri dentro uno span: il range 6..7 è la x.
    const html = '<span class="hljs-keyword">const</span> x';
    assert.equal(markRanges(html, [{ start: 6, end: 7 }]), `${html.slice(0, -1)}<mark>x</mark>`);
});

test('markRanges: un range dentro un tag lo marca senza toccare il tag', () => {
    const html = '<span class="hljs-keyword">const</span>';
    assert.equal(
        markRanges(html, [{ start: 0, end: 5 }]),
        '<span class="hljs-keyword"><mark>const</mark></span>',
    );
});

test('markRanges: un range a cavallo di un tag chiude e riapre invece di annidare male', () => {
    // 'ab' testo, poi <i>cd</i>: marcare 1..3 prende la b fuori e la c dentro.
    const out = markRanges('ab<i>cd</i>', [{ start: 1, end: 3 }]);
    assert.equal(out, 'a<mark>b</mark><i><mark>c</mark>d</i>');
});

test('markRanges: le entità valgono un carattere solo', () => {
    // Testo sorgente: 'a<b' — '<' è &lt; e conta 1. Il range 1..2 è quel carattere.
    assert.equal(markRanges('a&lt;b', [{ start: 1, end: 2 }]), 'a<mark>&lt;</mark>b');
});

test('markRanges: più range disgiunti sullo stesso html', () => {
    assert.equal(
        markRanges('abcdef', [
            { start: 0, end: 1 },
            { start: 4, end: 6 },
        ]),
        '<mark>a</mark>bcd<mark>ef</mark>',
    );
});

test('markRanges: un range fino a fine riga chiude il mark', () => {
    assert.equal(markRanges('abc', [{ start: 1, end: 3 }]), 'a<mark>bc</mark>');
});

test('rangesOf: gli offset seguono i segmenti concatenati', () => {
    assert.deepEqual(
        rangesOf([
            { text: 'return ', changed: false },
            { text: 'false', changed: true },
            { text: ';', changed: false },
        ]),
        [{ start: 7, end: 12 }],
    );
});

test('rangesOf: nessun segmento cambiato = nessun range', () => {
    assert.deepEqual(rangesOf([{ text: 'const x = 1', changed: false }]), []);
});

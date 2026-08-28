// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { markRanges, rangesOf } from '../core/mark-ranges.ts';

test('markRanges: with no ranges the html comes back untouched', () => {
    assert.equal(
        markRanges('<span class="k">const</span> x', []),
        '<span class="k">const</span> x',
    );
});

test('markRanges: marks a plain stretch of text', () => {
    assert.equal(markRanges('abcdef', [{ start: 2, end: 4 }]), 'ab<mark>cd</mark>ef');
});

test('markRanges: offsets are the text ones, not the html ones', () => {
    // 'const x' with the first five chars inside a span: the range 6..7 is the x.
    const html = '<span class="hljs-keyword">const</span> x';
    assert.equal(markRanges(html, [{ start: 6, end: 7 }]), `${html.slice(0, -1)}<mark>x</mark>`);
});

test('markRanges: a range inside a tag is marked without touching the tag', () => {
    const html = '<span class="hljs-keyword">const</span>';
    assert.equal(
        markRanges(html, [{ start: 0, end: 5 }]),
        '<span class="hljs-keyword"><mark>const</mark></span>',
    );
});

test('markRanges: a range straddling a tag closes and reopens instead of nesting badly', () => {
    // 'ab' as text, then <i>cd</i>: marking 1..3 takes the b outside and the c inside.
    const out = markRanges('ab<i>cd</i>', [{ start: 1, end: 3 }]);
    assert.equal(out, 'a<mark>b</mark><i><mark>c</mark>d</i>');
});

test('markRanges: entities count as a single character', () => {
    // Source text: 'a<b' — '<' is &lt; and counts as 1. The range 1..2 is that character.
    assert.equal(markRanges('a&lt;b', [{ start: 1, end: 2 }]), 'a<mark>&lt;</mark>b');
});

test('markRanges: several disjoint ranges on the same html', () => {
    assert.equal(
        markRanges('abcdef', [
            { start: 0, end: 1 },
            { start: 4, end: 6 },
        ]),
        '<mark>a</mark>bcd<mark>ef</mark>',
    );
});

test('markRanges: a range reaching the end of the line closes the mark', () => {
    assert.equal(markRanges('abc', [{ start: 1, end: 3 }]), 'a<mark>bc</mark>');
});

test('rangesOf: offsets follow the concatenated segments', () => {
    assert.deepEqual(
        rangesOf([
            { text: 'return ', changed: false },
            { text: 'false', changed: true },
            { text: ';', changed: false },
        ]),
        [{ start: 7, end: 12 }],
    );
});

test('rangesOf: no changed segment = no range', () => {
    assert.deepEqual(rangesOf([{ text: 'const x = 1', changed: false }]), []);
});

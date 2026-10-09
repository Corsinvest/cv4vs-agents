/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The diff preview as one HTML string. What is checked here is the shape diff.css draws from
// (one <div> per row, the gutter in an attribute) and that text reaching the string is escaped:
// this is the one place the preview writes HTML by hand.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { diffRowsHtml } from '../core/diff-html.ts';
import type { Row } from '../core/diff-rows.ts';

const row = (kind: Row['kind'], oldNo: number | null, newNo: number | null, text: string): Row => ({
    kind,
    oldNo,
    newNo,
    segs: [{ text, changed: false }],
});

test('one <div> per row, with the kind in the class', () => {
    const html = diffRowsHtml([row('ctx', 7, 7, 'a'), row('ins', null, 8, 'b')], '', true);
    assert.equal((html.match(/<div /g) ?? []).length, 2);
    assert.match(html, /<div class="cv-diff-row cv-diff-ctx" data-ln="7">a<\/div>/);
    assert.match(html, /<div class="cv-diff-row cv-diff-ins" data-ln="8">b<\/div>/);
});

test('a deleted row shows its old number, the only one it has', () => {
    assert.match(diffRowsHtml([row('del', 12, null, 'x')], '', true), /cv-diff-del" data-ln="12">/);
});

test('no gutter: the number is left out, not written empty', () => {
    // diff.css draws the number from the attribute: an empty one would still reserve the column.
    const html = diffRowsHtml([row('ins', null, 3, 'x')], '', false);
    assert.doesNotMatch(html, /data-ln/);
    assert.match(html, /<div class="cv-diff-row cv-diff-ins">x<\/div>/);
});

test('a hunk marker is a separator, with no text of its own', () => {
    const hunk: Row = { kind: 'hunk', oldNo: 40, newNo: 41, segs: [] };
    assert.equal(diffRowsHtml([hunk], '', true), '<div class="cv-diff-hunk"></div>');
});

test('a blank line keeps its row', () => {
    assert.equal(
        diffRowsHtml([row('ctx', 1, 1, '')], '', true),
        '<div class="cv-diff-row cv-diff-ctx" data-ln="1"></div>',
    );
});

test('plain text is escaped: a file can hold markup of its own', () => {
    const html = diffRowsHtml([row('ins', null, 1, '<script>alert("x")</script> & co')], '', true);
    assert.doesNotMatch(html, /<script>/);
    assert.match(html, /&lt;script&gt;alert\(&quot;x&quot;\)&lt;\/script&gt; &amp; co/);
});

test('plain text: the changed piece is marked, and escaped too', () => {
    const r: Row = {
        kind: 'ins',
        oldNo: null,
        newNo: 1,
        segs: [
            { text: 'a ', changed: false },
            { text: '<b>', changed: true },
            { text: ' c', changed: false },
        ],
    };
    assert.match(diffRowsHtml([r], '', true), />a <mark>&lt;b&gt;<\/mark> c<\/div>/);
});

test('a known language is highlighted, with the mark over the tokens', () => {
    const r: Row = {
        kind: 'ins',
        oldNo: null,
        newNo: 1,
        segs: [
            { text: 'const ', changed: false },
            { text: 'answer', changed: true },
            { text: ' = 42;', changed: false },
        ],
    };
    const html = diffRowsHtml([r], 'ts', true);
    assert.match(html, /<span class="hljs-keyword">const<\/span>/);
    assert.match(html, /<mark>answer<\/mark>/);
    assert.match(html, /<span class="hljs-number">42<\/span>/);
});

test('highlighted text is escaped by the highlighter, and stays so', () => {
    const html = diffRowsHtml([row('ins', null, 1, 'const a = "<img src=x>";')], 'ts', true);
    assert.doesNotMatch(html, /<img/);
    assert.match(html, /&lt;img src=x&gt;/);
});

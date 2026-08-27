/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stableMarkdownSplit } from '../core/markdown-split.ts';

test('split: cuts after a blank line', () => {
    const t = 'first paragraph\n\nsecond in progress';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'first paragraph\n\n');
});

test('split: never cuts inside an open code block', () => {
    const t = 'text\n\n```ts\nconst a = 1;\n\nconst b = 2;';
    const cut = stableMarkdownSplit(t);
    // The only safe point is the blank line BEFORE the fence.
    assert.equal(t.slice(0, cut), 'text\n\n');
});

test('split: resumes after a closed fence', () => {
    const t = 'text\n\n```ts\nconst a = 1;\n```\n\ntail in progress';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'text\n\n```ts\nconst a = 1;\n```\n\n');
});

test('split: the line closing a fence is already a cut point', () => {
    // With no blank line after the fence the block is finished anyway, and leaving it in the tail
    // means re-highlighting the whole of it on every pass.
    const t = 'text\n\n```ts\nconst a = 1;\n```\ntail in progress';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'text\n\n```ts\nconst a = 1;\n```\n');
});

test('split: a still-open fence is not a cut point', () => {
    const t = 'text\n\n```ts\nconst a = 1;';
    const cut = stableMarkdownSplit(t);
    assert.equal(t.slice(0, cut), 'text\n\n');
});

test('split: with no blank line it does not cut', () => {
    assert.equal(stableMarkdownSplit('a single paragraph that keeps growing'), 0);
});

test('split: empty text', () => {
    assert.equal(stableMarkdownSplit(''), 0);
});

test('split: never past the end of the text', () => {
    const t = 'paragraph\n\n';
    assert.equal(stableMarkdownSplit(t), 0);
});

test('split: a fence holding backticks does not throw the count off', () => {
    const t = 'a\n\n```md\nexample with ``` inside\n```\n\nb in progress';
    const cut = stableMarkdownSplit(t);
    assert.ok(cut > 0);
    // The cut never lands halfway through the block: the prefix has balanced fences.
    const fences = (t.slice(0, cut).match(/^\s*```/gm) ?? []).length;
    assert.equal(fences % 2, 0);
});

test('split: the prefix is always a line boundary', () => {
    const t = 'one\n\ntwo\n\nthree in progress';
    const cut = stableMarkdownSplit(t);
    assert.ok(cut === 0 || t[cut - 1] === '\n');
});

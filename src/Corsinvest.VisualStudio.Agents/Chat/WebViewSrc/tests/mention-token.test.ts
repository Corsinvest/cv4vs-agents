// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    composerMentionToken,
    mentionToken,
    replaceIfUnchanged,
    symbolMention,
} from '../core/path.ts';

// The forms below are the ones the CLI was seen to attach the right lines for (stream-json,
// claude-vscode entrypoint): relative, quoted with a space, absolute with a single line.

test('mentionToken: a whole file is the bare path', () => {
    assert.equal(mentionToken('src/Foo.cs'), '@src/Foo.cs');
});

test('mentionToken: a range is #Lstart-end, one line is #Lstart', () => {
    assert.equal(mentionToken('src/Foo.cs', 12, 18), '@src/Foo.cs#L12-18');
    assert.equal(mentionToken('src/Foo.cs', 20, 20), '@src/Foo.cs#L20');
    assert.equal(mentionToken('src/Foo.cs', 20, null), '@src/Foo.cs#L20');
});

test('mentionToken: a path with a space is quoted, range inside the quotes', () => {
    // Outside them the CLI's bare pattern stops at the space and references half a path.
    assert.equal(mentionToken('dir with space/b.txt'), '@"dir with space/b.txt"');
    assert.equal(mentionToken('dir with space/b.txt', 3, 4), '@"dir with space/b.txt#L3-4"');
});

// Backslashes, as the host sends them.
const WD = String.raw`K:\repo`;

test('composerMentionToken: a file inside the working directory is relative', () => {
    assert.equal(
        composerMentionToken(String.raw`K:\repo\src\Foo.cs`, WD, 3, 8),
        '@src/Foo.cs#L3-8',
    );
});

test('composerMentionToken: a file outside it stays absolute', () => {
    assert.equal(composerMentionToken(String.raw`D:\other\Bar.cs`, WD), '@D:/other/Bar.cs');
});

test('composerMentionToken: a folder keeps its slash, inside the quotes', () => {
    assert.equal(
        composerMentionToken(String.raw`K:\repo\src\Editor`, WD, null, null, true),
        '@src/Editor/',
    );
    assert.equal(
        composerMentionToken(String.raw`K:\repo\my dir`, WD, null, null, true),
        '@"my dir/"',
    );
});

test('composerMentionToken: the working directory itself is ./', () => {
    // A project at the root of the solution folder: an empty path would be a bare "@/".
    assert.equal(composerMentionToken(WD, WD, null, null, true), '@./');
});

// A picked symbol: the token, then its label as plain text the CLI ignores (the token ends at the
// space), so the prompt says which member the lines are.
test('symbolMention: token, then (label), then a space', () => {
    assert.equal(symbolMention('src/Foo.cs', 3, 9, 'Foo.Bar'), '@src/Foo.cs#L3-9 (Foo.Bar) ');
});

test('symbolMention quotes a path with a space', () => {
    assert.equal(
        symbolMention('dir name/Foo.cs', 3, 9, 'Foo.Bar'),
        '@"dir name/Foo.cs#L3-9" (Foo.Bar) ',
    );
});

test('symbolMention: one line is #Lstart', () => {
    assert.equal(symbolMention('src/Foo.cs', 5, 5, 'Foo'), '@src/Foo.cs#L5 (Foo) ');
});

test('symbolMention: no line is the path alone', () => {
    assert.equal(symbolMention('src/Foo.cs', 0, 0, 'Foo'), '@src/Foo.cs (Foo) ');
});

// The range arrives after the pick: the text written at pick time is upgraded only if it is still
// there, untouched, where it was written.
test('replaceIfUnchanged: swaps the text still in place', () => {
    assert.equal(
        replaceIfUnchanged('see @a#L5 (X) now', 4, '@a#L5 (X) ', '@a#L5-9 (X) '),
        'see @a#L5-9 (X) now',
    );
});

test('replaceIfUnchanged: null when the user edited or moved it', () => {
    assert.equal(replaceIfUnchanged('see @a#L5 (Y) now', 4, '@a#L5 (X) ', '@a#L5-9 (X) '), null);
    assert.equal(replaceIfUnchanged('', 4, '@a#L5 (X) ', '@a#L5-9 (X) '), null);
});

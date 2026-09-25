// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mentionToken } from '../core/path.ts';

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

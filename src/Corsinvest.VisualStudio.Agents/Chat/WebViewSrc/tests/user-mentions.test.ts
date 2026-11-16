// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitMentions } from '../core/user-mentions.ts';

// What a sent prompt shows as a file chip: the `@` mentions the CLI attaches, nothing else.
const mentions = (text: string) =>
    splitMentions(text)
        .filter((s) => s.kind === 'mention')
        .map((s) => (s.kind === 'mention' ? [s.text, s.path, s.startLine, s.endLine] : []));

test('splitMentions: plain text is one text segment', () => {
    assert.deepEqual(splitMentions('hello world'), [{ kind: 'text', text: 'hello world' }]);
});

test('splitMentions: a path, a range, the text around them kept in order', () => {
    assert.deepEqual(splitMentions('see @src/Foo.cs#L3-9 (Foo.Bar) now'), [
        { kind: 'text', text: 'see ' },
        { kind: 'mention', text: '@src/Foo.cs#L3-9', path: 'src/Foo.cs', startLine: 3, endLine: 9 },
        { kind: 'text', text: ' (Foo.Bar) now' },
    ]);
});

test('splitMentions: one line, no line, quoted path with a space', () => {
    assert.deepEqual(mentions('@a/b.ts#L7 and @README.md and @"dir name/x.cs#L2-4"'), [
        ['@a/b.ts#L7', 'a/b.ts', 7, 7],
        ['@README.md', 'README.md', 0, 0],
        ['@"dir name/x.cs#L2-4"', 'dir name/x.cs', 2, 4],
    ]);
});

test('splitMentions: trailing punctuation stays outside the chip', () => {
    assert.deepEqual(mentions('look at @src/Foo.cs.'), [['@src/Foo.cs', 'src/Foo.cs', 0, 0]]);
    assert.deepEqual(mentions('(see @a/b.ts)'), [['@a/b.ts', 'a/b.ts', 0, 0]]);
});

test('splitMentions: a heading fragment is dropped from the path', () => {
    assert.deepEqual(mentions('@docs/x.md#install'), [['@docs/x.md#install', 'docs/x.md', 0, 0]]);
});

test('splitMentions: names, emails, packages, agents and MCP resources are not files', () => {
    assert.deepEqual(
        mentions(
            'hi @Daniele, mail a@b.it, use @types/node and @babel/core, ask @agent-reviewer, ' +
                'read @github:repos/x/y and @"code-reviewer (agent)"',
        ),
        [],
    );
});

test('splitMentions: an absolute Windows path is a file', () => {
    assert.deepEqual(mentions(String.raw`@C:\src\Foo.cs#L1-2`), [
        [String.raw`@C:\src\Foo.cs#L1-2`, String.raw`C:\src\Foo.cs`, 1, 2],
    ]);
});

test('splitMentions: a mention at the start of a line after a newline', () => {
    assert.deepEqual(mentions('first\n@a/b.ts#L5'), [['@a/b.ts#L5', 'a/b.ts', 5, 5]]);
});

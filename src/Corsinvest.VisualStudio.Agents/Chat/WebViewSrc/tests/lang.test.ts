/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The extensions hljs does not know and the map has to carry. Measured over 3692 edits in real
// sessions: these three were the ones a diff still rendered plain for.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { highlightCode, langForFile } from '../core/lang.ts';

test('mdx, astro and .settings resolve to a language hljs has', () => {
    const cases: [string, string][] = [
        ['docs/src/content/docs/index.mdx', 'markdown'],
        ['docs/src/components/IconCard.astro', 'xml'],
        ['Properties/Settings.settings', 'xml'],
    ];
    for (const [file, lang] of cases) {
        assert.equal(langForFile(file), lang, file);
        assert.notEqual(highlightCode('<a href="x">y</a>', langForFile(file)), null, file);
    }
});

test('an extension nobody mapped still comes back as itself', () => {
    // highlightCode then answers null and the caller renders the text plain.
    assert.equal(langForFile('notes.zzz'), 'zzz');
    assert.equal(highlightCode('x', langForFile('notes.zzz')), null);
});

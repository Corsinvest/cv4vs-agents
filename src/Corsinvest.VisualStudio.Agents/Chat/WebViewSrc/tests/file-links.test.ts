/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The file-reference parser: every shape docs/file-links.md promises, and the non-cases the
// allow-list exists to block. Written against CURRENT behaviour — a failure here is either a bug or
// a promise in the doc the code doesn't keep, and the two must be told apart.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { findFileRefs, parseFileRef, firstRefHint } from '../core/file-links.ts';

/** The first ref of a text, or null. The assertions below almost always look at that one only. */
function first(text: string) {
    return findFileRefs(text)[0] ?? null;
}

// The seven shapes from the table in docs/file-links.md.

test('shape: name.ext:line', () => {
    const r = first('see ClientEvents.cs:208 for the rest');
    assert.equal(r?.path, 'ClientEvents.cs');
    assert.deepEqual(r?.lines, [208]);
    assert.equal(r?.match, 'ClientEvents.cs:208');
});

test('shape: relative path', () => {
    const r = first('in Core/Stats/StatsService.cs:45 it happens');
    assert.equal(r?.path, 'Core/Stats/StatsService.cs');
    assert.deepEqual(r?.lines, [45]);
});

test('shape: range, ends carries the last line', () => {
    const r = first('StatsService.cs:35-48 is the block');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('shape: range with an en-dash', () => {
    const r = first('StatsService.cs:35–48');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('shape: line list, one link each', () => {
    const r = first('AgentsPackage.cs:124,185,202 three spots');
    assert.deepEqual(r?.lines, [124, 185, 202]);
    assert.deepEqual(r?.ends, [124, 185, 202]);
});

test('shape: list whose elements are ranges', () => {
    const r = first('StatsService.cs:100-120,130');
    assert.deepEqual(r?.lines, [100, 130]);
    assert.deepEqual(r?.ends, [120, 130]);
});

test('shape: round brackets', () => {
    const r = first('in foo.cs(339) here');
    assert.equal(r?.path, 'foo.cs');
    assert.deepEqual(r?.lines, [339]);
});

test('shape: square brackets with a column', () => {
    const r = first('bar.ts[45:12] there');
    assert.equal(r?.path, 'bar.ts');
    assert.deepEqual(r?.lines, [45]);
});

test('shape: GitHub #L', () => {
    const r = first('ClaudeInstall.cs#L103 and there');
    assert.deepEqual(r?.lines, [103]);
});

test('shape: GitHub #L with a range', () => {
    const r = first('x.ts#L35-L48');
    assert.deepEqual(r?.lines, [35]);
    assert.deepEqual(r?.ends, [48]);
});

test('shape: GitHub # without the L', () => {
    const r = first('bar.ts#45');
    assert.deepEqual(r?.lines, [45]);
});

test('the column is parsed and dropped, it never becomes the end of a selection', () => {
    const r = first('x.ts:47:12 here');
    assert.deepEqual(r?.lines, [47]);
    assert.deepEqual(r?.ends, [47]);
});

// Several references in the same text.

test('two files on one line give two distinct refs', () => {
    const refs = findFileRefs('markdown.ts:57,file-links.ts:130');
    assert.equal(refs.length, 2);
    assert.equal(refs[0].path, 'markdown.ts');
    assert.equal(refs[1].path, 'file-links.ts');
});

test('refs come back in order and never overlap', () => {
    const refs = findFileRefs('first a.cs:1 then b.ts:2 last c.py:3');
    assert.deepEqual(
        refs.map((r) => r.path),
        ['a.cs', 'b.ts', 'c.py'],
    );
    for (let i = 1; i < refs.length; i++) {
        assert.ok(refs[i].start >= refs[i - 1].end, 'overlapping refs');
    }
});

// The non-cases: what the extension allow-list exists to NOT linkify.

test('a clock time is not a file', () => {
    assert.equal(first('at 10:30 in the evening'), null);
});

test('a host:port is not a file', () => {
    assert.equal(first('it runs on localhost.net:4040'), null);
});

test('a version is not a file', () => {
    assert.equal(first('the 2.1.220:5 from yesterday'), null);
});

test('a price is not a file', () => {
    assert.equal(first('it costs 19.99:2 euro'), null);
});

test('an unknown extension in prose stays text', () => {
    assert.equal(first('open report.xyzzy:12'), null);
});

test('a name with neither line nor folder is too noisy to linkify', () => {
    // "see the README.md" mid-sentence: no structure, no link.
    assert.equal(first('see the README.md and that is all'), null);
});

test('a name with a folder linkifies even without a line', () => {
    const r = first('it lives in docs/file-links.md then');
    assert.equal(r?.path, 'docs/file-links.md');
    assert.deepEqual(r?.lines, []);
});

// Path edge cases.

test('a folder with a dot in its name does not steal the anchor', () => {
    // "Corsinvest.VisualStudio.Agents" is a folder: the real ref is the last segment.
    const r = first('src/Corsinvest.VisualStudio.Agents/Chat/x.ts:5');
    assert.equal(r?.path, 'src/Corsinvest.VisualStudio.Agents/Chat/x.ts');
    assert.deepEqual(r?.lines, [5]);
});

test('an absolute windows path keeps its drive letter', () => {
    const r = first('open C:\\src\\repo\\Foo.cs:12 now');
    assert.equal(r?.path, 'C:\\src\\repo\\Foo.cs');
    assert.deepEqual(r?.lines, [12]);
});

test('a bare extension with no name is not a ref', () => {
    assert.equal(first('any old .ts file'), null);
});

test('brackets around the ref do not end up inside it', () => {
    const r = first('(NdjsonTransport.cs:91)');
    assert.equal(r?.path, 'NdjsonTransport.cs');
    assert.equal(r?.match, 'NdjsonTransport.cs:91');
});

test('duplicate lines in the list are dropped, the first one wins', () => {
    const r = first('x.cs:10,10,20');
    assert.deepEqual(r?.lines, [10, 20]);
});

test('empty text produces no ref', () => {
    assert.deepEqual(findFileRefs(''), []);
});

// parseFileRef: the token must be the ref and NOTHING else. This is also what inline code needs,
// where the content of a backtick is either a reference or a fragment of code.

test('parseFileRef accepts a token that is entirely a ref', () => {
    const r = parseFileRef('Foo.cs:12', 'known-ext');
    assert.equal(r?.path, 'Foo.cs');
    assert.deepEqual(r?.lines, [12]);
});

test('parseFileRef rejects a token that holds anything else', () => {
    assert.equal(parseFileRef('cat Foo.cs:12', 'known-ext'), null);
    assert.equal(parseFileRef('Foo.cs:12 and then', 'known-ext'), null);
});

test('parseFileRef rejects a version', () => {
    assert.equal(parseFileRef('1.5.0', 'known-ext'), null);
    assert.equal(parseFileRef('v2.1.237', 'known-ext'), null);
});

// plausible-path: the strictness for markdown hrefs, where the model already declared "this is a link".

test('plausible-path accepts an extension outside the allow-list', () => {
    const r = parseFileRef('src/utils/helper.rb#L12', 'plausible-path');
    assert.equal(r?.path, 'src/utils/helper.rb');
    assert.deepEqual(r?.lines, [12]);
});

test('plausible-path still rejects what has no file shape', () => {
    // An all-digit "extension" is a price or a version, never a file.
    assert.equal(parseFileRef('19.99#L2', 'plausible-path'), null);
});

test('plausible-path accepts a path with no line', () => {
    const r = parseFileRef('notes.md', 'plausible-path');
    assert.equal(r?.path, 'notes.md');
    assert.deepEqual(r?.lines, []);
});

// firstRefHint: the contract with marked. It must be a LOWER bound — never past the real start, or
// the tokenizer is never offered that position and the ref vanishes silently.

test('the hint never goes past the real start of the ref', () => {
    for (const t of [
        'see ClientEvents.cs:208 here',
        'open C:\\src\\Foo.cs:12 now',
        'in Core/Stats/StatsService.cs:45',
        '(NdjsonTransport.cs:91)',
    ]) {
        const hint = firstRefHint(t);
        const ref = first(t);
        assert.ok(ref, `no ref in "${t}"`);
        assert.ok(hint !== undefined, `no hint for "${t}"`);
        assert.ok(hint <= ref.start, `hint ${hint} past the start ${ref.start} in "${t}"`);
    }
});

test('no hint when there are no extensions', () => {
    assert.equal(firstRefHint('no file in here at all'), undefined);
});

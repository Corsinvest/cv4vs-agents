/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// What an inline `code span` does: it links when the content IS a reference, it stays text when
// it also contains anything else. The rule holds on the whole content — there are no partial links.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { renderCodespanInner as render } from '../core/codespan-link.ts';
import { parseFileRef } from '../core/file-links.ts';
import { escapeHtml } from '../core/html.ts';

// The real dependencies, not fakes: the rule only holds if it matches the parser that actually runs.
const renderCodespanInner = (text: string) => render(text, { parseFileRef, escapeHtml });

/** How many file links in the rendered fragment. */
function links(html: string): number {
    return (html.match(/class="cv-file-link"/g) ?? []).length;
}

function fileOf(html: string): string | null {
    return html.match(/data-file="([^"]*)"/)?.[1] ?? null;
}

// Links: the content is the reference and nothing else.

test('a reference with a line becomes a link', () => {
    const html = renderCodespanInner('ClientEvents.cs:208');
    assert.equal(links(html), 1);
    assert.equal(fileOf(html), 'ClientEvents.cs');
    assert.match(html, /data-line="208"/);
});

test('the label keeps what was written', () => {
    assert.match(renderCodespanInner('ClientEvents.cs:208'), />ClientEvents\.cs:208</);
});

test('a relative path links', () => {
    const html = renderCodespanInner('Core/Stats/StatsService.cs:45');
    assert.equal(fileOf(html), 'Core/Stats/StatsService.cs');
});

test('a range carries the end line in data-line-end', () => {
    const html = renderCodespanInner('StatsService.cs:35-48');
    assert.match(html, /data-line="35" data-line-end="48"/);
    assert.match(
        html,
        />StatsService\.cs:35-48</,
        'the label must not be normalized to the first line',
    );
});

test('a line list gives one link per line, the first with the file name', () => {
    const html = renderCodespanInner('AgentsPackage.cs:124,185,202');
    assert.equal(links(html), 3);
    assert.match(html, />AgentsPackage\.cs:124</);
    assert.match(html, />185</);
    assert.match(html, />202</);
});

test('the GitHub form links and keeps its own spelling', () => {
    const html = renderCodespanInner('ClaudeInstall.cs#L103');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="103"/);
    assert.match(html, />ClaudeInstall\.cs#L103</);
});

test('parentheses link', () => {
    const html = renderCodespanInner('foo.cs(339)');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="339"/);
});

test('a path with a folder links even without a line, opened at the top', () => {
    const html = renderCodespanInner('docs/file-links.md');
    assert.equal(links(html), 1);
    assert.match(html, /data-line="0"/);
});

// Does not link: there is something else besides the reference, or it is not a reference.

test('a command containing a reference stays text', () => {
    assert.equal(links(renderCodespanInner('cat Foo.cs:12')), 0);
});

test('a reference followed by something else stays text', () => {
    assert.equal(links(renderCodespanInner('Foo.cs:12 and then')), 0);
});

test('two references in one span stay text', () => {
    // Ambiguous: is the span a double citation or a fragment? No guessing.
    assert.equal(links(renderCodespanInner('a.cs:1 b.ts:2')), 0);
});

test('a version is not a file', () => {
    assert.equal(links(renderCodespanInner('1.5.0')), 0);
    assert.equal(links(renderCodespanInner('v2.1.237')), 0);
});

test('a time, a host and a price are not files', () => {
    assert.equal(links(renderCodespanInner('10:30')), 0);
    assert.equal(links(renderCodespanInner('localhost.net:4040')), 0);
    assert.equal(links(renderCodespanInner('19.99:2')), 0);
});

test('any code identifier stays text', () => {
    assert.equal(links(renderCodespanInner('appState.ideContext')), 0);
    assert.equal(links(renderCodespanInner('const x = 1')), 0);
});

test('a bare name with neither line nor folder stays text', () => {
    // Same threshold as prose: "README.md" on its own is too noisy to link.
    assert.equal(links(renderCodespanInner('README.md')), 0);
});

test('empty text does not throw', () => {
    assert.equal(renderCodespanInner(''), '');
});

// The produced HTML must stay safe: the content comes from the model.

test('unlinked text is escaped', () => {
    const html = renderCodespanInner('<script>alert(1)</script>');
    assert.doesNotMatch(html, /<script/);
    assert.match(html, /&lt;script&gt;/);
});

test('the path ends up escaped inside the attribute', () => {
    // A name with quotes would break data-file="..." if it were not escaped.
    const html = renderCodespanInner('a"b.cs:1');
    assert.doesNotMatch(html, /data-file="a"b/);
});

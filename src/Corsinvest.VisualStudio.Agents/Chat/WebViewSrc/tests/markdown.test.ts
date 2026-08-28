/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// That marked calls the right renderers: the half that tests over pure functions cannot see.
// The edge cases of WHAT counts as a reference live in codespan-link.test.ts and file-links.test.ts.
//
// LIMIT, worth knowing before adding tests here: renderMarkdown ends with DOMPurify, which needs a
// `window` and does not have one under node --test — `sanitize is not a function`, and the function
// degrades to its error branch. So what is tested here is the TOKENIZER, i.e. which tokens marked
// produces and with which renderer, not the final HTML. That would take jsdom (~10MB of
// devDependency) to verify a sanitisation that is DOMPurify's, not ours.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { marked } from 'marked';
import { closeOpenMarkdown } from '../core/markdown.ts'; // also pulls in renderer and extension

/** marked's HTML, before DOMPurify. */
function md(text: string): string {
    return marked.parse(text, { async: false }) as string;
}

function links(html: string): number {
    return (html.match(/class="cv-file-link"/g) ?? []).length;
}

// The three paths that produce a link, one per renderer.

test('prose: the fileLink extension catches a bare reference', () => {
    const html = md('see ClientEvents.cs:208 for the rest');
    assert.equal(links(html), 1);
    assert.match(html, /data-file="ClientEvents\.cs"/);
});

test('inline code: the codespan renderer catches a backticked reference', () => {
    // The form the model uses almost always: 33 times against 2 over 25 real sessions.
    const html = md('see `ClientEvents.cs:208` for the rest');
    assert.equal(links(html), 1);
    assert.match(html, /<code>/, 'the link lives INSIDE the code span, not in its place');
});

test('markdown link: the link renderer uses the href and keeps the label', () => {
    const html = md('[McpServerHost.cs:192](src/Mcp/McpServerHost.cs:192)');
    assert.equal(links(html), 1);
    assert.match(html, /data-file="src\/Mcp\/McpServerHost\.cs"/);
    assert.match(html, />McpServerHost\.cs:192</);
});

// The fence: the rule that must NOT fall together with the code span one.

test('fence: a reference inside a code block stays text', () => {
    assert.equal(links(md('```\ncat ClientEvents.cs:208\n```')), 0);
});

test('fence: not even with a declared language', () => {
    assert.equal(links(md('```bash\nvim Foo.cs:12\n```')), 0);
});

test('fence: the Copy button stays, and reads from the pre', () => {
    // A link inside the block would break the copy: cv-copy-btn takes its text from the sibling <pre>.
    const html = md('```\nFoo.cs:12\n```');
    assert.match(html, /<cv-copy-btn[^>]*frompre="1"/);
});

test('table: the Copy button carries the markdown source, not the DOM', () => {
    // Pipes and the alignment row must survive: that is what makes the paste re-parsable.
    const src = '| a | b |\n|:--|--:|\n| 1 | 2 |';
    const html = md(src + '\n');
    assert.match(html, /<div class="cv-md-table-wrap">/);
    assert.match(html, /<cv-copy-btn[^>]*class="cv-md-table-copy-btn"/);
    const text = /<cv-copy-btn[^>]*text="([^"]*)"/.exec(html)?.[1] ?? '';
    assert.equal(text.replace(/&quot;/g, '"').replace(/&amp;/g, '&'), src);
});

test('table: the cells stay rendered by marked', () => {
    assert.equal(links(md('| file |\n|---|\n| ClientEvents.cs:208 |\n')), 1);
});

test('table: a scroll level wraps the table, inside the button wrap', () => {
    // Measured in the chat: a table never overflows the bubble, it compresses — at 12 columns every
    // header wraps letter by letter (144px tall against 29). Hence two levels: the outer one stays
    // the positioning context so the copy button holds still, the inner one takes the overflow.
    // A single level and the button scrolls away with the columns.
    const html = md('| a | b |\n|---|---|\n| 1 | 2 |\n');
    assert.match(
        html,
        /<div class="cv-md-table-wrap"><div class="cv-md-table-scroll"><table/,
        'the scroll level goes INSIDE the wrap and around the table',
    );
    assert.match(
        html,
        /<\/table>\s*<\/div><cv-copy-btn/,
        'the button is the scroll level sibling, not its child',
    );
});

// The attributes the host reads on click. They do not go through DOMPurify here, but markdown.ts's
// ADD_ATTR must list them all: data-line-end was missing while the renderer had always emitted it,
// and a range opened without selecting.

test('a range emits data-line-end, in prose and in backticks', () => {
    for (const src of ['look at StatsService.cs:35-48', 'look at `StatsService.cs:35-48`']) {
        const html = md(src);
        assert.match(html, /data-line="35"/, src);
        assert.match(html, /data-line-end="48"/, src);
    }
});

test('a list of lines becomes one link per line, in both forms', () => {
    assert.equal(links(md('AgentsPackage.cs:124,185,202 three spots')), 3);
    assert.equal(links(md('`AgentsPackage.cs:124,185,202`')), 3);
});

test('an http href stays an external link, not a file', () => {
    const html = md('[doc](https://example.com/a.cs:12)');
    assert.equal(links(html), 0);
    assert.match(html, /href="https:\/\/example\.com/);
});

test('a code span that is not a reference stays a code span', () => {
    const html = md('run `npm run build` now');
    assert.equal(links(html), 0);
    assert.match(html, /<code>npm run build<\/code>/);
});

// DOMPurify's ADD_ATTR. We cannot run sanitize here (it needs a window), but the way that bug shows
// up is an attribute emitted by the renderers and not listed in the allow-list: comparing the two
// sets catches it without needing a DOM.

test('every attribute the renderers emit is in DOMPurify ADD_ATTR', () => {
    const src = new URL('../core/markdown.ts', import.meta.url);
    const allowed = new Set(
        (readFileSync(src, 'utf8').match(/ADD_ATTR:\s*\[([^\]]*)\]/)?.[1] ?? '')
            .split(',')
            .map((s) => s.trim().replace(/^['"]|['"]$/g, ''))
            .filter(Boolean),
    );
    // Always allowed by DOMPurify, no need to declare them.
    const builtin = new Set(['class', 'href', 'title', 'rel', 'src', 'alt', 'id', 'style']);

    // A markdown that goes through EVERY renderer emitting attributes of ours.
    const html = md(
        'prose Foo.cs:12-20 and `Bar.ts:34` and [x](src/Baz.cs:5)\n\n```ts\nconst a = 1;\n```',
    );
    const emitted = new Set([...html.matchAll(/\s([a-z-]+)="/g)].map((m) => m[1]));
    const missing = [...emitted].filter((a) => !allowed.has(a) && !builtin.has(a));
    assert.deepEqual(missing, [], `attributes DOMPurify would drop: ${missing.join(', ')}`);
});

// closeOpenMarkdown: the streaming path, which runs on EVERY token. It closes open constructs so a
// partial chunk renders stably instead of flipping the layout halfway through a response.

test('streaming: an open fence gets closed', () => {
    assert.equal(closeOpenMarkdown('text\n```ts\nconst a = 1;'), 'text\n```ts\nconst a = 1;\n```');
});

test('streaming: an already closed fence is left alone', () => {
    const t = 'text\n```ts\nconst a = 1;\n```\ntail';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: an unpaired backtick on the last line gets closed', () => {
    assert.equal(closeOpenMarkdown('see `Foo.cs:12'), 'see `Foo.cs:12`');
});

test('streaming: an even number of backticks stays as it is', () => {
    const t = 'see `Foo.cs:12` here';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: backticks inside an open fence are literal', () => {
    // Inside a block backticks are not inline code: closing the fence is enough, adding an unpaired
    // backtick too would break the block.
    assert.equal(closeOpenMarkdown('```\nuse `x`'), '```\nuse `x`\n```');
});

test('streaming: an unpaired backtick on a PREVIOUS line is left alone', () => {
    // Only the last line is under construction; a previous one is already as the model wrote it.
    const t = 'line with an unpaired `\nfinished line';
    assert.equal(closeOpenMarkdown(t), t);
});

test('streaming: empty text stays empty', () => {
    assert.equal(closeOpenMarkdown(''), '');
});

test('streaming: a half-written reference does not link, a complete one does', () => {
    // The case that matters on every token: the text grows under the render. A name without a line
    // does not qualify (too noisy), so the link appears once the ref is really finished — and does
    // not flicker while the digits arrive one at a time.
    assert.equal(links(md(closeOpenMarkdown('see `Foo.cs'))), 0, 'no line: no link');
    assert.equal(links(md(closeOpenMarkdown('see `Foo.cs:12`'))), 1, 'complete: link');
});

test('streaming: digits arriving one at a time move only the line, not the link', () => {
    // "Foo.cs:1" is already a valid reference (line 1), so the link is there from the start and the
    // line updates as it goes. Better that than a link appearing and vanishing: the bubble stays put.
    for (const [src, line] of [
        ['see `Foo.cs:1`', '1'],
        ['see `Foo.cs:12`', '12'],
        ['see `Foo.cs:123`', '123'],
    ] as const) {
        const html = md(closeOpenMarkdown(src));
        assert.equal(links(html), 1, src);
        assert.match(html, new RegExp(`data-line="${line}"`), src);
    }
});

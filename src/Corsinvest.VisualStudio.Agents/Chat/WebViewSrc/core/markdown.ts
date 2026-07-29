/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Markdown → sanitized HTML pipeline. Pure string-only; output is
// DOMPurified and fed to lit-html via `unsafeHTML`.

import { marked, type Renderer, type Tokens } from 'marked';
import DOMPurify from 'dompurify';
import { resolveLang, highlightCode } from './lang';
import { escapeHtml } from './html';
import { findFileRefs, firstRefHint, parseFileRef } from './file-links';

// CLI-internal tags that look like HTML but are content; DOMPurify would
// drop them silently, so escape up-front to render as literal text.
const _LITERAL_TAG_RE =
    /<\/?(?:tool_use_error|system-reminder|ide_[a-z_]+|local-command-(?:stdout|stderr)|post-tool-use-hook|browser_instruction|browser|terminal|function_calls|invoke|parameter)\b[^>]*>/gi;

// Configure marked once with our custom code/link renderers.
const renderer = new marked.Renderer() as Renderer & {
    code: (token: Tokens.Code) => string;
    link: (token: Tokens.Link) => string;
};

renderer.code = function (token: Tokens.Code): string {
    const code = token.text ?? '';
    const lang = resolveLang(token.lang ?? '');
    const hl = highlightCode(code, lang);
    const language = hl !== null ? lang : 'plaintext';
    // <cv-copy-btn> is upgraded on innerHTML parse; it reads its text from
    // the sibling <pre> at click time (`frompre` attribute).
    return (
        `<div class="cv-md-code-wrap">` +
        `<pre><code class="hljs language-${language}">${hl ?? escapeHtml(code)}</code></pre>` +
        `<cv-copy-btn class="cv-md-copy-btn" frompre="1" title="Copy"></cv-copy-btn>` +
        `</div>`
    );
};

renderer.link = function (token: Tokens.Link): string {
    const href = token.href || '';
    const title = token.title || '';
    const text = token.text || '';
    if (/^(https?|mailto):/.test(href)) {
        return `<a href="${escapeHtml(href)}" title="${escapeHtml(title)}" target="_blank" rel="noopener noreferrer">${text}</a>`;
    }
    // A markdown link to a local file — the model writes [X.cs:192](src/.../X.cs:192). If the href
    // parses as a file ref, render a clickable file link (cv-message routes the click to VS) using
    // the LABEL text as-is. Otherwise fall back to plain text.
    // 'plausible-path', not the prose allow-list: the model wrote this AS a link, so any extension is
    // fine (.rb/.kt/.tf … used to be dropped here). Unlike VS Code we still degrade to text when the
    // href isn't path-shaped at all, instead of leaving a blue link that does nothing on click.
    const ref = parseFileRef(href, 'plausible-path');
    if (ref) {
        const line = ref.lines[0] ?? 0;
        return `<a class="cv-file-link" data-file="${escapeHtml(ref.path)}" data-line="${line}" title="Open in editor">${escapeHtml(text)}</a>`;
    }
    return escapeHtml(text);
};

// Inline extension: turn a bare "path:line" reference (ClientEvents.cs:208, Core/x.ts:45) into a
// clickable link that cv-message routes to VS. It runs on inline TEXT only — marked tokenizes fenced
// code and inline `code` first, so those are never touched, and http(s) autolinks are consumed by
// marked's own inline link/url rules before this. The heavy lifting (which substrings qualify, the
// allow-list, the :line[:col] suffix shapes) lives in findFileRefs; here we just anchor it to the
// current cursor. Idempotent by construction: marked always re-parses from the markdown source.
const fileLinkExtension = {
    name: 'fileLink',
    level: 'inline' as const,
    // Where marked should jump to next — a potential index, per marked's contract. firstRefHint
    // shares ANCHOR/BACK with the parser, so it can't drift the way a parallel hand-written regex
    // did (that is how "#L10" in prose stayed unlinked once already).
    start: firstRefHint,
    tokenizer(src: string) {
        // A file-ref only counts if it starts at the cursor (offset 0 of the remaining src). Only the
        // first ref can qualify — findFileRefs returns them in order — so stop at it.
        const ref = findFileRefs(src)[0];
        if (!ref || ref.start !== 0) {
            return undefined;
        }
        return {
            type: 'fileLink',
            raw: ref.match,
            path: ref.path,
            lines: ref.lines,
            ends: ref.ends,
        };
    },
    renderer(token: { path: string; lines: number[]; ends: number[]; raw: string }): string {
        const file = escapeHtml(token.path);
        // data-line-end carries the last line of a range so the host selects the whole block
        // (Options → Chat → SelectLinesOnOpen); it equals data-line for a single line.
        const anchor = (label: string, line: number, end = line): string =>
            `<a class="cv-file-link" data-file="${file}" data-line="${line}" data-line-end="${end}" title="Open in editor">${label}</a>`;
        // No line → single link on the whole path (file:// scheme dropped from the label as noise).
        if (token.lines.length === 0) {
            return anchor(escapeHtml(token.path.replace(/^file:\/\/\/?/i, '')), 0);
        }
        // Single line → label the link with what the model actually wrote, so a range keeps its end
        // ("x.ts:100-120" stays readable) and "#L128" keeps its GitHub shape. Only the file:// scheme
        // is dropped, as noise.
        if (token.lines.length === 1) {
            return anchor(
                escapeHtml(token.raw.replace(/^file:\/\/\/?/i, '')),
                token.lines[0],
                token.ends[0],
            );
        }
        // "file.cs:124,185,202" → the first link carries "file.cs:124", the rest are just the bare
        // line numbers (same file, different line), commas as plain text between them. Each label is
        // the element as written, so a range inside the list keeps its end ("100-120,130").
        const name = escapeHtml(token.path.replace(/^file:\/\/\/?/i, ''));
        const written = token.raw.slice(token.raw.indexOf(':') + 1).split(',');
        const [first, ...rest] = token.lines;
        let html = anchor(
            `${name}:${escapeHtml(written[0] ?? String(first))}`,
            first,
            token.ends[0],
        );
        rest.forEach((ln, i) => {
            html += ',' + anchor(escapeHtml(written[i + 1] ?? String(ln)), ln, token.ends[i + 1]);
        });
        return html;
    },
};

marked.use({ gfm: true, breaks: true, renderer, extensions: [fileLinkExtension] });

/**
 * Render markdown to sanitized HTML for `unsafeHTML` (DOMPurify has run).
 * On parse failure, falls back to escaped plaintext instead of a blank bubble.
 */
export function renderMarkdown(text: string | undefined | null): string {
    const normalized = (text ?? '').replace(_LITERAL_TAG_RE, (m) => escapeHtml(m));
    try {
        const html = marked.parse(normalized, { async: false }) as string;
        return DOMPurify.sanitize(html, {
            ADD_ATTR: ['target', 'frompre', 'data-file', 'data-line'],
            ADD_TAGS: ['cv-copy-btn'],
        });
    } catch (err) {
        return `<pre>${escapeHtml(text ?? '')}</pre><div style="color:var(--danger);font-size:11px">⚠ markdown error: ${escapeHtml(String(err))}</div>`;
    }
}

/**
 * Temporarily close markdown constructs left open by mid-stream text, so the
 * partial render is stable (no code-fence swallowing the rest, no half-open
 * bold/italic). The closers are only for *this* render; the next chunk re-runs
 * on the real (still-growing) text, and the final render uses `renderMarkdown`
 * on the complete text. Same sanitized pipeline → same safety.
 */
function closeOpenMarkdown(text: string): string {
    // Walk lines tracking whether we're inside a fenced code block. A line that
    // *starts* with ``` flips the state — so ``` that appear as content (not at
    // line start, or while already inside a block) don't throw off the count.
    // This is the key difference from a naive `/^```/gm` tally, which miscounts
    // a code block whose content shows other fences (markdown-in-markdown).
    const lines = text.split('\n');
    let inFence = false;
    for (const line of lines) {
        if (/^\s*```/.test(line)) {
            inFence = !inFence;
        }
    }
    if (inFence) {
        // Unclosed code block → close it so the partial render stays a code box
        // instead of swallowing everything after it.
        return text + '\n```';
    }
    // Outside any code block: balance an odd inline-code backtick on the tail
    // line (inside a block backticks are literal, so we skip that case above).
    const lastLine = text.slice(text.lastIndexOf('\n') + 1);
    const ticks = (lastLine.match(/`/g) ?? []).length;
    return ticks % 2 === 1 ? text + '`' : text;
}

/**
 * Render markdown for the in-flight (streaming) assistant text. Closes open
 * constructs first so a partial chunk renders stably, then runs the normal
 * sanitized pipeline. Use `renderMarkdown` for the final, complete text.
 */
export function renderMarkdownStreaming(text: string | undefined | null): string {
    return renderMarkdown(closeOpenMarkdown(text ?? ''));
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Markdown → sanitized HTML pipeline. Pure string-only; output is
// DOMPurified and fed to lit-html via `unsafeHTML`.

import { marked, type Renderer, type Tokens } from 'marked';
import DOMPurify from 'dompurify';
import hljs from 'highlight.js';
import { resolveLang } from './lang';
import { escapeHtml } from './html';

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
    const language = hljs.getLanguage(lang) ? lang : 'plaintext';
    let highlighted: string;
    try {
        highlighted =
            language !== 'plaintext' ? hljs.highlight(code, { language }).value : escapeHtml(code);
    } catch {
        highlighted = escapeHtml(code);
    }
    // <cv-copy-btn> is upgraded on innerHTML parse; it reads its text from
    // the sibling <pre> at click time (`frompre` attribute).
    return (
        `<div class="cv-md-code-wrap">` +
        `<pre><code class="hljs language-${language}">${highlighted}</code></pre>` +
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
    // Local file paths render as plain text — `<cv-message>` adds its own
    // click handlers; don't smuggle file:// URLs through the renderer.
    return text;
};

marked.use({ gfm: true, breaks: true, renderer });

/**
 * Render markdown to sanitized HTML for `unsafeHTML` (DOMPurify has run).
 * On parse failure, falls back to escaped plaintext instead of a blank bubble.
 */
export function renderMarkdown(text: string | undefined | null): string {
    const normalized = (text ?? '').replace(_LITERAL_TAG_RE, (m) => escapeHtml(m));
    try {
        const html = marked.parse(normalized, { async: false }) as string;
        return DOMPurify.sanitize(html, {
            ADD_ATTR: ['target', 'frompre'],
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

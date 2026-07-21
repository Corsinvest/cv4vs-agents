/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Diff rendering helper: wraps diff2html-ui into a small `renderDiff` API,
// with theme syncing wired once.

// Use `diff2html-ui-base` (no internal hljs) to avoid shipping highlight.js
// twice, and disable its `highlightCode()` since it strips word-diff
// <ins>/<del> markup — we re-highlight every code line ourselves below.
import { Diff2HtmlUI } from 'diff2html/lib/ui/js/diff2html-ui-base';
import { ColorSchemeType } from 'diff2html/lib/types';
import { state } from '../core/state';
import hljs from 'highlight.js';

export type DiffFormat = 'line-by-line' | 'side-by-side';

export const SPLIT_THRESHOLD = 600;

// `data-lang` (file extension) from diff2html → hljs language id. Kept separate
// from core/lang's ALIASES on purpose: ALIASES also rewrites the diff header's
// visible filename via patchPathFor, so mapping cs→csharp there would show
// "Foo.csharp". These short aliases only resolve the hljs language for diffs.
const LANG_MAP: Record<string, string> = {
    ts: 'typescript',
    tsx: 'typescript',
    js: 'javascript',
    jsx: 'javascript',
    cs: 'csharp',
    py: 'python',
    rb: 'ruby',
    md: 'markdown',
    sh: 'bash',
};

function colorScheme(): ColorSchemeType {
    return state.theme === 'light' ? ColorSchemeType.LIGHT : ColorSchemeType.DARK;
}

/** Apply hljs to every code line of a freshly drawn diff. Lines that
 *  contain word-diff markup (<ins>/<del>) are highlighted segment-by-segment
 *  so the inline change boxes survive the highlight. */
function highlightLines(targetEl: HTMLElement): void {
    const wrap = targetEl.querySelector('.d2h-file-wrapper');
    const rawLang = wrap?.getAttribute('data-lang')?.trim() ?? '';
    const hljsLang = LANG_MAP[rawLang] ?? rawLang;
    if (!hljsLang || !hljs.getLanguage(hljsLang)) {
        return;
    }
    const hl = (s: string): string =>
        hljs.highlight(s, { language: hljsLang, ignoreIllegals: true }).value;
    targetEl.querySelectorAll('.d2h-code-line-ctn').forEach((el) => {
        const text = el.textContent ?? '';
        if (!text.trim()) {
            return;
        }
        const hasWordDiff = el.querySelector('ins, del');
        try {
            if (!hasWordDiff) {
                el.innerHTML = hl(text);
                return;
            }
            // Highlight text nodes and <ins>/<del> inner text separately so
            // the word-diff wrappers stay intact.
            const out: string[] = [];
            for (const node of Array.from(el.childNodes)) {
                if (node.nodeType === Node.TEXT_NODE) {
                    out.push(hl(node.textContent ?? ''));
                } else if (node.nodeType === Node.ELEMENT_NODE) {
                    const e = node as HTMLElement;
                    const tag = e.tagName.toLowerCase();
                    if (tag === 'ins' || tag === 'del') {
                        out.push(`<${tag}>${hl(e.textContent ?? '')}</${tag}>`);
                    } else {
                        out.push(e.outerHTML);
                    }
                }
            }
            el.innerHTML = out.join('');
        } catch {
            /* ignore — leave the plain text */
        }
    });
}

/**
 * Render `patch` into `targetEl` using diff2html, then highlight the code
 * lines via hljs (we bypass diff2html's own highlighter — see comment above).
 */
export function renderDiff(targetEl: HTMLElement, patch: string, outputFormat: DiffFormat): void {
    const ui = new Diff2HtmlUI(targetEl, patch, {
        outputFormat,
        drawFileList: false,
        matching: 'lines',
        // Word-diff intra-line only on very similar pairs (VS Code-like).
        matchWordsThreshold: 0.8,
        synchronisedScroll: true,
        fileListToggle: false,
        fileContentToggle: false,
        stickyFileHeaders: false,
        highlight: false,
        colorScheme: colorScheme(),
    });
    ui.draw();
    highlightLines(targetEl);
}

// Re-apply the color-scheme CSS class on theme change instead of re-rendering.
state.on('theme', () => {
    const dark = colorScheme() === ColorSchemeType.DARK;
    document.querySelectorAll('.d2h-wrapper').forEach((el) => {
        el.classList.toggle('d2h-dark-color-scheme', dark);
        el.classList.toggle('d2h-light-color-scheme', !dark);
    });
});

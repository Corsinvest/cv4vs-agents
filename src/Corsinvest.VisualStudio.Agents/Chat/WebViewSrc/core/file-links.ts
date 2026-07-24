/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Find "file:line" references in assistant text so the UI can turn them into clickable links that
// open the file in VS. Pure, side-effect free, no DOM.
//
// Approach follows VS Code's terminalLinkParsing (two phases, not one mega-regex):
//   1. peel an optional :line[:col] / (line,col) / [line:col] / "line N" suffix off the END,
//   2. what's left in front is the path candidate — non-space, no bracket openers.
// We diverge from VS Code on ONE point: we REQUIRE a known code/text extension on the path. VS Code
// (a terminal, paths everywhere) links anything and lets click-time resolution filter; in a chat the
// model always writes an extension (X.cs:20), so the allow-list kills false positives (10:30 clock,
// localhost.net:4040 host:port) up-front instead of rendering dead links. URLs are NOT handled here —
// marked's autolink/renderer.link consumes http(s) before this ever runs.

/** Extensions we linkify — code/text files that make sense to open in the editor. Hardcoded (these
 *  are universal; no user option needed). Extensionless files (Makefile, Dockerfile) aren't covered
 *  by design — add here if they ever come up. */
const LINKABLE_EXT = new Set([
    'cs',
    'ts',
    'tsx',
    'js',
    'jsx',
    'mjs',
    'cjs',
    'css',
    'scss',
    'html',
    'xaml',
    'xml',
    'json',
    'md',
    'yml',
    'yaml',
    'cpp',
    'cc',
    'h',
    'hpp',
    'c',
    'py',
    'go',
    'rs',
    'java',
    'sql',
    'sh',
    'ps1',
    'razor',
    'cshtml',
    'vb',
    'txt',
    'csproj',
    'sln',
    'props',
    'targets',
    'config',
    'editorconfig',
    'gitignore',
]);

/** One found reference: the exact matched substring (so the caller can wrap it in place), the parsed
 *  path, and the 1-based line(s) — usually one, but a "file.cs:124,185,202" list yields several
 *  (each becomes its own link at the same path). Empty `lines` = no line (open at top).
 *  start/end are indices into the source text. */
export interface FileRef {
    match: string;
    path: string;
    lines: number[];
    start: number;
    end: number;
}

// Phase 1 — a line[:col] suffix, matched at the END of a single (space-delimited) token. Covers:
//   :339  :339:12  :35-48 (range, opens at the first line)  :124,185,202 (list → one link per line)
//   (339)  (339,12)  [339]  [339:12].
// The col/range-end are captured but unused (we open at the start line). A range uses a hyphen or an
// en/em-dash ("35–48"); a list uses commas ("124,185"). The verbose ", line 339" form spans a space
// and can't ride in one token — dropped on purpose (the model writes X.cs:20, never "X.cs, line 20").
const SUFFIX =
    /(?::(?<r1>\d+(?:,\d+)*)(?:[-–—]\d+)?(?::\d+)?|\((?<r2>\d+)(?:,\d+)?\)|\[(?<r3>\d+)(?::\d+)?\])$/;

// A path candidate: no whitespace, and not starting with a bracket/pipe/angle (VS Code's set). We
// additionally require it to CONTAIN a dot (so there's an extension to check). Slashes/backslashes,
// drive letters and file:// are all allowed inside.
const PATH = /(?:file:\/\/\/?)?[^\s|<>[({][^\s|<>]*/g;

function extOf(path: string): string {
    const clean = path.replace(/[\\/]+$/, '');
    const dot = clean.lastIndexOf('.');
    if (dot < 0) {
        return '';
    }
    return clean.slice(dot + 1).toLowerCase();
}

/** Scan `text` for "path:line" references whose path has a linkable extension. Returns them in order,
 *  non-overlapping. The caller wraps each [start,end) in a link. */
export function findFileRefs(text: string): FileRef[] {
    const refs: FileRef[] = [];
    if (!text) {
        return refs;
    }
    // Walk word-ish tokens (non-space runs). For each, peel the suffix, validate the remaining path.
    // A token is any maximal run of non-space characters — the space is the delimiter (so
    // "pippo.txt :020" splits into two tokens and never links). A ; inside a token splits it too:
    // prose like "(a.cs:1; b.cs:2)" packs two refs in one run, so break on ; as well.
    const TOKEN = /[^\s;]+/g;
    let m: RegExpExecArray | null;
    while ((m = TOKEN.exec(text)) !== null) {
        let tok = m[0];
        let lead = 0;
        // Strip a leading ( [ { the model wraps a ref in — "(a.cs:1)" → "a.cs:1)". Track `lead` so
        // start/end stay accurate.
        const lm = tok.match(/^[([{]+/);
        if (lm) {
            lead = lm[0].length;
            tok = tok.slice(lead);
        }
        // Strip a trailing ) ] } , . that is prose punctuation — BUT only if the token has no
        // matching opener left (we already removed a leading one, or there was none). A genuine
        // (339)/[45] line suffix keeps its bracket because that bracket has no leading partner here.
        tok = tok.replace(/[)\]},.]+$/, (t) => {
            const hasOpener = /[([{]/.test(tok);
            return hasOpener ? t : '';
        });
        const tokStart = m.index + lead;
        // Peel the line[:col] suffix off the token's tail; what's left in front is the path candidate.
        const sm = tok.match(SUFFIX);
        let lines: number[] = [];
        let pathPart = tok;
        if (sm) {
            const raw = sm.groups?.r1 ?? sm.groups?.r2 ?? sm.groups?.r3 ?? '';
            // r1 may be a comma list ("124,185,202"); r2/r3 are single. Split and de-dup.
            lines = [
                ...new Set(
                    raw
                        .split(',')
                        .map(Number)
                        .filter((n) => n > 0),
                ),
            ];
            pathPart = tok.slice(0, sm.index);
        }
        // Validate the path: single PATH match spanning the whole remaining part, with a known ext.
        PATH.lastIndex = 0;
        const pm = PATH.exec(pathPart);
        if (!pm || pm.index !== 0 || pm[0] !== pathPart) {
            continue;
        }
        if (!LINKABLE_EXT.has(extOf(pathPart))) {
            continue;
        }
        // A path with no line is still linkable (open at top). But require SOME structure: either a
        // separator (/ or \) or the suffix — a bare "README.md" mid-sentence is too noisy to link.
        if (lines.length === 0 && !/[\\/]/.test(pathPart)) {
            continue;
        }
        const matchStr = sm ? pathPart + tok.slice(sm.index) : pathPart;
        refs.push({
            match: matchStr,
            path: pathPart,
            lines,
            start: tokStart,
            end: tokStart + matchStr.length,
        });
    }
    return refs;
}

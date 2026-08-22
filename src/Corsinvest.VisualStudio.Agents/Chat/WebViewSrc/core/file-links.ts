/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Find "file:line" references in assistant text so the UI can turn them into clickable links that
// open the file in VS. Pure, side-effect free, no DOM.
//
// The EXTENSION is the anchor — it is the only part that says "this is a file" (a ':' is also
// host:port and a clock, a ',' is also punctuation). So we scan for ".ext" occurrences and grow
// outwards from each one:
//   1. anchor  — ".ext", validated against LINKABLE_EXT (prose) or "has a letter" (hrefs),
//   2. back    — the path, up to the first char that can't sit in one (space, comma, bracket, …),
//   3. forward — the :line / (line) / [line] / #Lline suffix; anything past it is just text.
// Growing from the anchor is what keeps two refs in one run apart ("a.ts:57,b.ts:57" → two links):
// an end-anchored suffix regex would match the LAST ':57' and swallow everything before it.
// URLs are NOT handled here — marked's autolink/renderer.link consumes http(s) before this runs.

/** Extensions linkified in PROSE ("x.cs:20" written mid-sentence). A markdown link written by the
 *  model is checked differently — see RefStrictness — so this list only guards the blind scan, where
 *  it is the one thing separating ".cs" from ".net"/".com". Grouped by area; the user adds anything
 *  missing via Options -> Chat -> "Extra linkable extensions" (merged in by setExtraLinkableExtensions).
 *  Extensionless files (Makefile, Dockerfile) aren't covered by design. */
const BUILT_IN_EXT = [
    // .NET / Visual Studio
    'cs',
    'csx',
    'vb',
    'vbs',
    'fs',
    'fsi',
    'fsx',
    'fsscript',
    'razor',
    'cshtml',
    'vbhtml',
    'csproj',
    'vbproj',
    'fsproj',
    'sln',
    'props',
    'targets',
    'config',
    'resx',
    'xaml',
    'axaml',
    'aspx',
    'ascx',
    'asax',
    'ashx',
    'asmx',
    'master',
    'sitemap',
    'skin',
    'browser',
    'vsct',
    'vsixmanifest',
    'vstemplate',
    'ruleset',
    'settings',
    'shproj',
    'projitems',
    'slnf',
    'slnx',
    'sqlproj',
    'wixproj',
    'wxs',
    'wxi',
    // web / TypeScript
    'ts',
    'tsx',
    'mts',
    'cts',
    'js',
    'jsx',
    'mjs',
    'cjs',
    'coffee',
    'vue',
    'svelte',
    'astro',
    'njk',
    'liquid',
    'twig',
    'html',
    'htm',
    'xhtml',
    'shtml',
    'css',
    'scss',
    'sass',
    'less',
    'styl',
    'hbs',
    'handlebars',
    'mustache',
    'pug',
    'jade',
    'ejs',
    'webmanifest',
    // C / C++
    'c',
    'h',
    'cc',
    'cpp',
    'cxx',
    'c++',
    'hh',
    'hpp',
    'hxx',
    'h++',
    'ixx',
    'cppm',
    'inl',
    'ipp',
    'tlh',
    'tli',
    'idl',
    'odl',
    'rc',
    'rc2',
    'def',
    'cu',
    'cuh',
    // JVM / .NET-adjacent
    'java',
    'jsp',
    'jspx',
    'aj',
    'kt',
    'kts',
    'scala',
    'sc',
    'clj',
    'cljs',
    'cljc',
    'groovy',
    'gradle',
    'sbt',
    // scripting
    'py',
    'pyi',
    'rb',
    'rbs',
    'rake',
    'gemspec',
    'php',
    'phtml',
    'pl',
    'pm',
    'lua',
    'tcl',
    // systems
    'go',
    'rs',
    'zig',
    'nim',
    'odin',
    'cr',
    'v',
    'd',
    'swift',
    'dart',
    'pas',
    'asm',
    // functional
    'hs',
    'lhs',
    'elm',
    'erl',
    'hrl',
    'ex',
    'exs',
    'gleam',
    'ml',
    'mli',
    'purs',
    'res',
    'resi',
    'rkt',
    'scm',
    'ss',
    'lisp',
    'el',
    'fnl',
    // scientific
    'jl',
    'r',
    'm',
    'mm',
    'f90',
    'f95',
    'f03',
    'ipynb',
    // web3
    'sol',
    'move',
    'cairo',
    // shell and system scripting (unix + windows)
    'sh',
    'bash',
    'zsh',
    'fish',
    'nu',
    'ps1',
    'psm1',
    'psd1',
    'ps1xml',
    'psrc',
    'pssc',
    'bat',
    'cmd',
    'btm',
    'ksh',
    'csh',
    'tcsh',
    'ahk',
    'awk',
    'sed',
    'service',
    'timer',
    'socket',
    'desktop',
    'reg',
    'inf',
    'wsf',
    'vbe',
    'jse',
    // shaders
    'glsl',
    'hlsl',
    'hlsli',
    'wgsl',
    'metal',
    'cginc',
    'compute',
    'usf',
    'ush',
    'fx',
    'fxh',
    // data / markup / config
    'json',
    'jsonc',
    'json5',
    'jsonld',
    'xml',
    'xsd',
    'xsl',
    'xslt',
    'dtd',
    'yml',
    'yaml',
    'toml',
    'ini',
    'cfg',
    'conf',
    'properties',
    'env',
    'csv',
    'tsv',
    'sql',
    'graphql',
    'gql',
    'proto',
    'thrift',
    'avsc',
    'wsdl',
    'http',
    'rest',
    'plist',
    'nuspec',
    // docs
    'md',
    'mdx',
    'markdown',
    'rst',
    'adoc',
    'asciidoc',
    'tex',
    'txt',
    'text',
    // build / tooling
    'cmake',
    'mk',
    'mak',
    'ninja',
    'bazel',
    'bzl',
    'just',
    'lock',
    'tf',
    'tfvars',
    'hcl',
    'bicep',
    'dockerfile',
    'nix',
    'containerfile',
    'editorconfig',
    'gitignore',
    'gitattributes',
    'npmrc',
    'eslintrc',
    'prettierrc',
    'babelrc',
    // editors
    'vim',
    'vimrc',
];

/** Built-ins plus whatever the user added in Options. Rebuilt by setExtraLinkableExtensions. */
let LINKABLE_EXT = new Set(BUILT_IN_EXT);

/** Merge the user's "Extra linkable extensions" (bare, lowercase — normalized host-side) on top of
 *  the built-ins. Called when VS options arrive, and again whenever they change. */
export function setExtraLinkableExtensions(extra: readonly string[] | null | undefined): void {
    LINKABLE_EXT = new Set(BUILT_IN_EXT);
    for (const e of extra ?? []) {
        if (e) {
            LINKABLE_EXT.add(e.toLowerCase());
        }
    }
}

/** One found reference: the exact matched substring (so the caller can wrap it in place), the parsed
 *  path, and the 1-based line(s) — usually one, but a "file.cs:124,185,202" list yields several
 *  (each becomes its own link at the same path). Empty `lines` = no line (open at top).
 *  `ends` is parallel to `lines`: the last line of a range ("100-120" → lines[0]=100, ends[0]=120),
 *  or the same value when the element is a single line. The host selects lines..ends on open.
 *  start/end are indices into the source text. */
export interface FileRef {
    match: string;
    path: string;
    lines: number[];
    ends: number[];
    start: number;
    end: number;
}

// Step 1, the anchor: every ".ext" in the text (1-10 chars — past that it is prose, not a suffix),
// not followed by more word chars. Global — one run of text can hold several refs.
const ANCHOR = /\.([A-Za-z0-9]{1,10})(?![A-Za-z0-9])/g;

// Step 2, backwards: the path chars that may precede the dot. Excludes the separators that delimit a
// ref from its neighbours (space, comma, semicolon, brackets) and the suffix introducers (: #), so a
// second ref in the same run is never swallowed. Anchored to the end of the slice = grows leftwards.
const BACK = /[^\s|<>[({,;:#'"`]*$/;

// Step 3, forwards: the line suffix right after the extension. Covers
//   :339  :339:12  :35-48 (range → opens at the first line)  :124,185,202 (list → one link per line)
//   :100-120,130 and :10,20-30,40 (the two mixed — every element of the list may be a range)
//   (339)  (339,12)  [339]  [339:12]  #339  #L339  #L35-L48 (GitHub form, common in markdown hrefs).
// The column and the range ends are matched but dropped — we open at each start line. A range takes a
// hyphen or an en/em-dash; a list takes commas. The verbose ", line 339" form is out by design (the
// model writes X.cs:20, never "X.cs, line 20").
const RANGE_LIST = String.raw`\d+(?:[-–—]\d+)?(?:,\d+(?:[-–—]\d+)?)*`;
const FWD = new RegExp(
    `^(?::(?<r1>${RANGE_LIST})(?::\\d+)?|\\((?<r2>\\d+)(?:,\\d+)?\\)|\\[(?<r3>\\d+)(?::\\d+)?\\]|#L?(?<r4>\\d+(?:[-–—]L?\\d+)?))`,
);

// A windows drive letter, which BACK cuts off because of its ':' — re-attached when it sits right
// before the path ("k:\src\README.md:5").
const DRIVE = /[A-Za-z]:$/;

/** How strict the path check is. Prose is scanned blind, so it needs the extension allow-list to keep
 *  "localhost.net:4040" / "19.99:2" from rendering as dead links; a markdown href was written as a link
 *  by the model on purpose, so any plausible path shape is enough. */
export type RefStrictness = 'known-ext' | 'plausible-path';

/** Scan `text` for file references, growing outwards from each ".ext" anchor. Returns them in order,
 *  non-overlapping. The caller wraps each [start,end) in a link. */
export function findFileRefs(text: string, strictness: RefStrictness = 'known-ext'): FileRef[] {
    const refs: FileRef[] = [];
    if (!text) {
        return refs;
    }
    ANCHOR.lastIndex = 0;
    let m: RegExpExecArray | null;
    while ((m = ANCHOR.exec(text)) !== null) {
        const ext = m[1].toLowerCase();
        // Prose needs the allow-list (it is scanned blind); an href only needs an extension that
        // isn't all digits — "2.1.220#L5" and "19.99#L2" are a version and a price, never a file.
        if (strictness === 'known-ext' ? !LINKABLE_EXT.has(ext) : !/[a-z]/.test(ext)) {
            continue;
        }
        const dot = m.index;
        const afterExt = m.index + m[0].length;
        // A separator right after the "extension" means it was a dotted DIRECTORY name, not the end
        // of the file ("src/Corsinvest.VisualStudio.Agents/…/x.ts" — the real anchor is the last one).
        if (/^[\\/]/.test(text.slice(afterExt))) {
            continue;
        }
        let head = text.slice(0, dot).match(BACK)?.[0] ?? '';
        if (!head) {
            continue; // a bare ".ts" with no name in front
        }
        let start = dot - head.length;
        const drive = DRIVE.test(text.slice(0, start));
        if (drive && /^[\\/]/.test(head)) {
            start -= 2;
            head = text.slice(start, dot);
        }
        const fm = text.slice(afterExt).match(FWD);
        const raw = fm
            ? (fm.groups?.r1 ?? fm.groups?.r2 ?? fm.groups?.r3 ?? fm.groups?.r4 ?? '')
            : '';
        // r1 may be a comma list whose elements are ranges ("100-120,130"); r2/r3 are single, r4 may
        // be a "35-L48" range. Each element yields a start line (where the link opens) and an end
        // line (the host selects down to it) — equal when the element is a single line.
        const lines: number[] = [];
        const ends: number[] = [];
        for (const part of raw.split(',')) {
            const [a, b] = part.split(/[-–—]/).map((n) => Number(n.replace(/^L/i, '')));
            if (!(a > 0) || lines.includes(a)) {
                continue; // drop junk and duplicates, keeping the first occurrence
            }
            lines.push(a);
            ends.push(b > a ? b : a);
        }
        // A path with no line is still linkable (open at top). In prose, require SOME structure —
        // either a separator or the suffix — since a bare "README.md" mid-sentence is too noisy.
        // An href is explicit, so "[label](notes.md)" links fine.
        if (strictness === 'known-ext' && lines.length === 0 && !/[\\/]/.test(head)) {
            continue;
        }
        const end = afterExt + (fm ? fm[0].length : 0);
        refs.push({
            match: text.slice(start, end),
            path: text.slice(start, afterExt),
            lines,
            ends,
            start,
            end,
        });
        ANCHOR.lastIndex = end; // never let the next anchor land inside this ref
    }
    return refs;
}

/** Parse a standalone token (a markdown href) as a single file reference, or null. */
export function parseFileRef(token: string, strictness: RefStrictness): FileRef | null {
    const [ref] = findFileRefs(token, strictness);
    return ref && ref.start === 0 && ref.match === token ? ref : null;
}

/** Cheap lower bound for where the first ref could start — for marked's `start()`, which wants a
 *  *potential* index, not an exact one. The full scan costs ~2ms on a long message and marked calls
 *  start() once per inline token, which streaming multiplies by every re-render. Derived from the same
 *  ANCHOR/BACK pair as the real parser so it can't drift: a hand-written hint that misses a shape
 *  silently stops the tokenizer from ever being offered that position. */
export function firstRefHint(text: string): number | undefined {
    ANCHOR.lastIndex = 0;
    const m = ANCHOR.exec(text);
    if (!m) {
        return undefined;
    }
    // Back up over the path characters in front of the dot — the ref starts there, not at the dot.
    const start = m.index - (text.slice(0, m.index).match(BACK)?.[0].length ?? 0);
    // BACK stops at the ':' of a windows drive, so the real ref can begin 2 chars earlier. The hint
    // must never sit past the real start or marked would cut the drive off.
    return DRIVE.test(text.slice(0, start)) ? start - 2 : start;
}

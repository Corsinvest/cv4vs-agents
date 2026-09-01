/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import hljs from 'highlight.js';

// Language aliases for highlight.js, and the two ways in: `resolveLang` for a fence label,
// `langForFile` for a path. Used by the markdown code block renderer, the diff preview and its
// patch header, and Write's body.
//
// Only entries that hljs does NOT recognise natively. hljs already covers:
// bash/sh/zsh, json/jsonc/json5, xml/html/xhtml/plist/svg, dockerfile,
// groovy, gradle, makefile/mk/mak, yaml/yml, py, rb, ts/tsx/mts/cts, etc.
// See https://github.com/highlightjs/highlight.js/blob/main/SUPPORTED_LANGUAGES.md

/**
 * Map a fence label, a file extension, or a whole filename to a hljs-supported language.
 * Add an entry here when authors hit a "no highlighting" fence in the wild.
 *
 * One map rather than two: the keys never collide — extensions on one side, extensionless
 * filenames on the other — and splitting them meant every caller had to know which of the two to
 * ask, so most asked neither. Not exported for the same reason: `langForFile` and `resolveLang`
 * are the two questions worth asking, and a caller reaching past them into the table is a caller
 * about to get a dotfile or a suffixed name wrong.
 */
const LANGS: Record<string, string> = {
    // .NET project / build / config (XML)
    csproj: 'xml',
    vbproj: 'xml',
    vcxproj: 'xml',
    fsproj: 'xml',
    sqlproj: 'xml',
    shproj: 'xml',
    njsproj: 'xml',
    proj: 'xml',
    props: 'xml',
    targets: 'xml',
    config: 'xml',
    resx: 'xml',
    nuspec: 'xml',
    ruleset: 'xml',
    manifest: 'xml',
    appxmanifest: 'xml',
    vsixmanifest: 'xml',
    slnx: 'xml',
    vsct: 'xml',

    // Razor / WPF / Xamarin / WinUI / Avalonia / Android markup (XML).
    // cshtml/razor would need highlightjs-cshtml-razor (separate package);
    // xml is a sane fallback.
    xaml: 'xml',
    cshtml: 'xml',
    razor: 'xml',
    vbhtml: 'xml',
    axaml: 'xml',
    axml: 'xml',

    // Frontend frameworks (no native hljs support)
    vue: 'xml',
    svelte: 'xml',

    // Solution / config (INI-like)
    sln: 'ini',
    editorconfig: 'ini',
    gitconfig: 'ini',
    properties: 'ini',
    env: 'ini',

    // Containers / orchestration
    containerfile: 'dockerfile',
    compose: 'yaml',

    // Shell variants not covered by hljs (bash already covers sh/zsh; ps1 is native,
    // the module/manifest extensions are not)
    psm1: 'powershell',
    psd1: 'powershell',
    ksh: 'bash',
    fish: 'bash',
    bashrc: 'bash',
    zshrc: 'bash',

    // JSON variants (jsonl, webmanifest are not native)
    jsonl: 'json',
    webmanifest: 'json',

    // Plain text fallbacks
    gitignore: 'plaintext',
    gitattributes: 'plaintext',
    dockerignore: 'plaintext',
    npmignore: 'plaintext',
    prettierignore: 'plaintext',
    eslintignore: 'plaintext',
    log: 'plaintext',
    txt: 'plaintext',

    // Whole filenames, for files that carry no extension at all. `containerfile` is up with the
    // container entries above — it reads as an extension too, and one entry serves both.
    // Taken from GitHub Linguist's `filenames`, keeping the ones a .NET/web/Windows repo actually
    // holds: its full list runs to 130 names, most of them ecosystems nobody opens in this IDE.
    dockerfile: 'dockerfile',
    makefile: 'makefile',
    gnumakefile: 'makefile',
    bsdmakefile: 'makefile',
    kbuild: 'makefile',
    rakefile: 'ruby',
    gemfile: 'ruby',
    podfile: 'ruby',
    brewfile: 'ruby',
    fastfile: 'ruby',
    appfile: 'ruby',
    guardfile: 'ruby',
    capfile: 'ruby',
    cmakelists: 'cmake',
    jenkinsfile: 'groovy',
    vagrantfile: 'ruby',
    justfile: 'makefile',
    procfile: 'yaml',
    caddyfile: 'nginx',
    codeowners: 'plaintext',
    pkgbuild: 'bash',
    gradlew: 'bash',
    bash_profile: 'bash',
    bash_aliases: 'bash',
    bash_logout: 'bash',
    cshrc: 'bash',
    kshrc: 'bash',
    profile: 'bash',
    pylintrc: 'ini',
    browserslist: 'plaintext',
};

/**
 * The keys of LANGS that are whole filenames rather than extensions — the ones a suffix can be
 * appended to (`Dockerfile.prod`) and still name the same kind of file. Kept apart because the map
 * holds both kinds and a stem lookup against all of it would read `env.config` as an `env` file.
 */
const WHOLE_FILE_NAMES = new Set([
    'dockerfile',
    'containerfile',
    'makefile',
    'gnumakefile',
    'bsdmakefile',
    'rakefile',
    'gemfile',
    'podfile',
    'brewfile',
    'cmakelists',
    'jenkinsfile',
    'vagrantfile',
    'justfile',
    'procfile',
    'caddyfile',
]);

/**
 * Resolve a fence label or extension to a hljs language name.
 * Returns the lowercase input itself when no alias matches — hljs handles
 * the unknown-language fallback.
 */
export function resolveLang(label: string | undefined | null): string {
    const lc = (label ?? '').toLowerCase();
    return LANGS[lc] || lc;
}

/**
 * The hljs language for a file path — the question every caller actually has, asked once here
 * instead of four times as "what is the extension?".
 *
 * Three shapes, in the order that makes them unambiguous:
 *  - a whole filename (`Dockerfile`, `Makefile`) — has no extension to find;
 *  - a dotfile (`.gitignore`, `.editorconfig`) — where `lastIndexOf('.')` is 0, so the name IS
 *    the key, with the leading dot dropped;
 *  - an extension (`Foo.csproj`).
 *
 * Filename first: `.editorconfig` read as an extension would answer `ini` and lose the name, and
 * a file called `Dockerfile.prod` should be a Dockerfile, not a `prod`.
 *
 * Returns '' when there is nothing to go on, which highlightCode treats as "render it plain".
 */
export function langForFile(filePath: string | undefined | null): string {
    const base = (filePath ?? '').split(/[\\/]/).pop()?.toLowerCase() ?? '';
    if (!base) {
        return '';
    }
    const byName = LANGS[base] ?? LANGS[base.replace(/^\./, '')];
    if (byName) {
        return byName;
    }
    const dot = base.lastIndexOf('.');
    // A suffixed filename — `Dockerfile.prod`, `Makefile.am` — is still that file, so the stem is
    // worth a look before the suffix is taken for an extension. Only against the names, though:
    // `env` and `config` are both keys, and reading `env.config` by its stem would answer for the
    // wrong half of it.
    if (dot > 0 && WHOLE_FILE_NAMES.has(base.slice(0, dot))) {
        return LANGS[base.slice(0, dot)];
    }
    // dot === 0 is a dotfile the map does not know: its name is not an extension, so there is
    // nothing left to try.
    const ext = dot > 0 ? base.slice(dot + 1) : '';
    return ext ? (LANGS[ext] ?? ext) : '';
}

// Bounded like the markdown one, and for the same reason: the callers are Lit render() bodies, so
// a tool row re-highlights its whole body whenever anything about the row changes. Only settled
// text benefits — a growing code fence is a new key on every pass, so the streaming path (which
// goes through renderMarkdown's own cache) neither hits this nor thrashes it.
const HL_CACHE_MAX = 100;
const _hlCache = new Map<string, string | null>();

/** Drop the memoized highlights. Paired with clearMarkdownCache() — same lifetime, same reason. */
export function clearHighlightCache(): void {
    _hlCache.clear();
}

/**
 * Highlight `code` as `label` (a fence label or a file extension), returning HTML.
 * Null when the language is unknown or hljs throws — the caller then renders the text
 * plain, which is what an unhighlighted file should look like anyway.
 * Memoized by code+language — see HL_CACHE_MAX.
 */
export function highlightCode(code: string, label: string | undefined | null): string | null {
    const language = resolveLang(label);
    if (!language || !hljs.getLanguage(language)) {
        return null;
    }
    const key = `${language} ${code}`;
    const cached = _hlCache.get(key);
    // has() and not a null check: null is a real result (hljs threw) and worth keeping.
    if (cached !== undefined || _hlCache.has(key)) {
        _hlCache.delete(key);
        _hlCache.set(key, cached ?? null);
        return cached ?? null;
    }
    let out: string | null;
    try {
        out = hljs.highlight(code, { language, ignoreIllegals: true }).value;
    } catch {
        out = null;
    }
    _hlCache.set(key, out);
    if (_hlCache.size > HL_CACHE_MAX) {
        _hlCache.delete(_hlCache.keys().next().value as string);
    }
    return out;
}

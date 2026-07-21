/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Language alias maps for highlight.js. Used by both the markdown code
// block renderer (fence label → language) and the diff renderer (file
// extension → language for the patch header).
//
// Only entries that hljs does NOT recognise natively. hljs already covers:
// bash/sh/zsh, json/jsonc/json5, xml/html/xhtml/plist/svg, dockerfile,
// groovy, gradle, makefile/mk/mak, yaml/yml, py, rb, ts/tsx/mts/cts, etc.
// See https://github.com/highlightjs/highlight.js/blob/main/SUPPORTED_LANGUAGES.md

/**
 * Map fence label / extension to a hljs-supported language.
 * Add an entry here when authors hit a "no highlighting" fence in the wild.
 */
export const ALIASES: Record<string, string> = {
    // .NET project / build / config (XML)
    csproj: 'xml',
    vbproj: 'xml',
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
    slnx: 'xml',

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

    // Shell variants not covered by hljs (bash already covers sh/zsh)
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
    log: 'plaintext',
    txt: 'plaintext',
};

/**
 * Special filenames without an extension → language. Used by the diff
 * renderer to compute the patch header path so hljs picks the right
 * language for files like `Dockerfile` or `Makefile`.
 */
export const FILE_NAME_LANGS: Record<string, string> = {
    dockerfile: 'dockerfile',
    containerfile: 'dockerfile',
    makefile: 'makefile',
    gnumakefile: 'makefile',
    rakefile: 'ruby',
    gemfile: 'ruby',
    podfile: 'ruby',
    cmakelists: 'cmake',
    jenkinsfile: 'groovy',
    vagrantfile: 'ruby',
};

/**
 * Resolve a fence label or extension to a hljs language name.
 * Returns the lowercase input itself when no alias matches — hljs handles
 * the unknown-language fallback.
 */
export function resolveLang(label: string | undefined | null): string {
    const lc = (label ?? '').toLowerCase();
    return ALIASES[lc] || lc;
}

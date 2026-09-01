/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Patch text construction for diffs. Pure, no DOM — consumed by diff-rows.ts
// (inline preview) and diff-stats.ts (+N/-M counts).

import { createPatch } from 'diff';
import { langForFile } from './lang';
import { normPath } from './path';

/**
 * Rewrite/append the path's extension so the name we hand to highlightCode
 * resolves to a language it understands (e.g. `Dockerfile` -> a known ext).
 */
export function patchPathFor(filePath: string | undefined | null): string {
    const path = normPath(filePath) || 'file';
    const lang = langForFile(path);
    if (!lang) {
        return path;
    }
    // Swap the extension when there is one, append when there is not: `Foo.csproj` becomes
    // `Foo.xml`, `Dockerfile` becomes `Dockerfile.dockerfile`. A dotfile appends too — `.gitignore`
    // has no extension to replace, and `.gitignore.plaintext` is a name hljs can read.
    const baseName = path.split('/').pop() ?? path;
    const dot = baseName.lastIndexOf('.');
    const stem = dot > 0 ? path.slice(0, path.length - (baseName.length - dot)) : path;
    return `${stem}.${lang}`;
}

/**
 * Build a unified patch via jsdiff. `context` = lines around each hunk
 * (`Number.MAX_SAFE_INTEGER` for the whole file). `ignoreWhitespace` is
 * passed in by callers so core/ stays free of the state import.
 */
export function buildPatch(
    oldStr: string | undefined | null,
    newStr: string | undefined | null,
    filePath: string | undefined | null,
    context: number,
    ignoreWhitespace = false,
): string {
    return createPatch(patchPathFor(filePath), oldStr ?? '', newStr ?? '', '', '', {
        context,
        ignoreWhitespace,
        stripTrailingCr: true,
    });
}

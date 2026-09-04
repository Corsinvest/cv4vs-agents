/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Patch path naming for diffs. Pure, no DOM — consumed by diff-rows.ts
// (inline preview) and diff-stats.ts (+N/-M counts).

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

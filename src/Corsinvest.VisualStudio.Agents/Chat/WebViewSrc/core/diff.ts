/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Patch text construction for diffs. Pure, no DOM — the renderer (which
// pulls in diff2html and touches `document`) lives in ui/diff.ts.

import { createPatch } from 'diff';
import { ALIASES, FILE_NAME_LANGS } from './lang';
import { normPath } from './path';

/**
 * Rewrite/append the path's extension so diff2html's header gives highlight.js
 * a language it understands (e.g. `Dockerfile` -> a known ext).
 */
export function patchPathFor(filePath: string | undefined | null): string {
    const path = normPath(filePath) || 'file';
    const baseName = path.split('/').pop() ?? path;
    const dot = baseName.lastIndexOf('.');
    if (dot <= 0) {
        const alias = FILE_NAME_LANGS[baseName.toLowerCase()];
        return alias ? `${path}.${alias}` : path;
    }
    const ext = baseName.slice(dot + 1).toLowerCase();
    const alias = ALIASES[ext];
    return alias ? path.slice(0, path.length - ext.length) + alias : path;
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

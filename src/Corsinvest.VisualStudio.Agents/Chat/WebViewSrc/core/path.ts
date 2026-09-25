/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Path manipulation helpers. Pure, side-effect free, no DOM.

/**
 * Normalize a path to forward slashes (idempotent).
 */
export function normPath(p: string | undefined | null): string {
    return (p ?? '').replace(/\\/g, '/');
}

/**
 * Path relative to the working directory, or unchanged (forward-slashed) if
 * outside it; "" when equal to it. Compare is case-insensitive, slash/trailing-slash tolerant.
 */
export function relPath(
    filePath: string | undefined | null,
    workingDirectory: string | undefined | null,
): string {
    if (!filePath) {
        return '';
    }
    const rootBare = normPath(workingDirectory).replace(/\/+$/, '');
    const pathBare = normPath(filePath).replace(/\/+$/, '');
    if (!rootBare) {
        return pathBare;
    }
    if (pathBare.toLowerCase() === rootBare.toLowerCase()) {
        return '';
    }
    const rootPrefix = rootBare + '/';
    return pathBare.toLowerCase().startsWith(rootPrefix.toLowerCase())
        ? pathBare.slice(rootPrefix.length)
        : pathBare;
}

/**
 * The `@…` token for a path, quoted when it contains a space.
 *
 * The CLI reads attachments with two patterns, `@"<path>"` and a bare `@<path>` that stops at
 * the first whitespace — so an unquoted path with a space in it silently references only the
 * part before the space. The user has no way to get this right by hand: it is the page that
 * writes the token. The `#L10-20` range goes inside the quotes: it is part of what the CLI parses,
 * and it reads only those lines. One line is `#L10` — the CLI takes `#L10-10` too, nobody writes it.
 */
export function mentionToken(
    path: string,
    startLine?: number | null,
    endLine?: number | null,
): string {
    const range =
        startLine == null
            ? ''
            : endLine == null || endLine === startLine
              ? `#L${startLine}`
              : `#L${startLine}-${endLine}`;
    const target = path + range;
    return target.includes(' ') ? `@"${target}"` : `@${target}`;
}

export function fileName(path: string | undefined | null): string {
    return normPath(path).split('/').pop() ?? '';
}

/**
 * Path formatted for display in a tool row, with the NATIVE separator
 * (backslash on Windows, like VS Code and the rest of the IDE).
 * `relative` (the "Show relative paths" option): when true, paths under the
 * workdir are shortened relative to it; when false, the full path is shown.
 * `relPath` normalizes to `/` for comparison; we re-apply `\` for Windows
 * workspaces (workdir has a drive letter or a backslash).
 */
export function displayPath(
    filePath: string | undefined | null,
    workingDirectory: string | undefined | null,
    relative = true,
): string {
    const shown = relative ? relPath(filePath, workingDirectory) : normPath(filePath);
    const isWindows = /^[A-Za-z]:|\\/.test(workingDirectory ?? '');
    return isWindows ? shown.replace(/\//g, '\\') : shown;
}

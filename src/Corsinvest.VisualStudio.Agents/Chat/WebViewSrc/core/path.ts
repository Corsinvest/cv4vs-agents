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
 * Basename of a path (last segment after the final `/` or `\`).
 */
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

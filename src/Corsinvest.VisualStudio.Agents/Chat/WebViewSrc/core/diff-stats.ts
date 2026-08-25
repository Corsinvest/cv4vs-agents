/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Change counts for the tool row title. Pure, no DOM.

import { structuredPatch } from 'diff';
import { patchPathFor } from './diff';

/**
 * Lines actually added and removed, counted from the hunks — not the net
 * difference in line count. Replacing 14 lines with 1 is `+1 -14`, which is
 * what git reports; the net (`-13`) describes no real edit.
 */
export function countChanges(
    oldStr: string | undefined | null,
    newStr: string | undefined | null,
    filePath: string | undefined | null,
    ignoreWhitespace = false,
): { added: number; removed: number } {
    const patch = structuredPatch(
        patchPathFor(filePath),
        patchPathFor(filePath),
        oldStr ?? '',
        newStr ?? '',
        '',
        '',
        { context: 0, ignoreWhitespace, stripTrailingCr: true },
    );
    let added = 0;
    let removed = 0;
    for (const hunk of patch.hunks) {
        for (const line of hunk.lines) {
            if (line[0] === '+') {
                added++;
            } else if (line[0] === '-') {
                removed++;
            }
        }
    }
    return { added, removed };
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Unified-patch trimming. Its own module, with no imports: plain text in, plain text out,
// so it stays testable without pulling in jsdiff and the language tables `diff.ts` needs.

/**
 * Cut a patch down to `maxLines` of hunk content, dropping whole hunks past the cap and
 * keeping the file header. Returns `patch` unchanged when it already fits.
 *
 * diff2html emits one row per patch line, so an inline preview that clips with `max-height`
 * still builds every row of a large edit: a file with many scattered changes mounts hundreds
 * of nodes for the dozen it shows. Cutting the patch is what keeps them out of the DOM —
 * nothing is lost, since the expand dialog re-renders from the full strings.
 */
export function clampPatch(patch: string, maxLines: number): string {
    const lines = patch.split('\n');
    const kept: string[] = [];
    let inHunk = false;
    let rows = 0;
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const startsHunk = line.startsWith('@@');
        // Everything before the first hunk is the file header: free, and diff2html needs it to
        // name the file. From there on every line is a rendered row, `@@` included.
        if (startsHunk) {
            // Take a hunk only if all of it fits, so the cut never leaves a header with no rows
            // under it — diff2html would draw an empty file block. The first one is taken
            // whatever its size and truncated below: dropping it would render nothing at all.
            let size = 1;
            while (i + size < lines.length && !lines[i + size].startsWith('@@')) {
                size++;
            }
            if (rows > 0 && rows + size > maxLines) {
                break;
            }
            inHunk = true;
        } else if (inHunk && rows + 1 > maxLines) {
            break;
        }
        if (inHunk) {
            rows++;
        }
        kept.push(line);
    }
    return kept.length === lines.length ? patch : `${kept.join('\n')}\n`;
}

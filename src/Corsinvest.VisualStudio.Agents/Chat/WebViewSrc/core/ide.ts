/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Parse `<ide_*>` and related context tags the CLI injects into user
// messages. Returns structured data; the UI layer is responsible for
// turning each ref into a chip element.

import type { IdeContextRef } from './types';

const _IDE_TAG_RE =
    /<(?:ide_[a-z_]+|terminal|browser|browser_instruction|local-command-stdout|local-command-stderr|post-tool-use-hook)[^>]*>[\s\S]*?<\/(?:ide_[a-z_]+|terminal|browser|browser_instruction|local-command-stdout|local-command-stderr|post-tool-use-hook)>\n?/g;

/**
 * Extract `<ide_opened_file>` and `<ide_selection>` blocks from a user
 * message body. Strips the matching tag content from `text` and returns
 * a list of bare file/line references.
 */
export function parseIdeContextTags(text: string | undefined | null): {
    text: string;
    refs: IdeContextRef[];
} {
    const refs: IdeContextRef[] = [];
    const cleaned = (text ?? '')
        .replace(_IDE_TAG_RE, (match) => {
            const openedFile = match.match(/<ide_opened_file>([\s\S]*?)<\/ide_opened_file>/);
            if (openedFile) {
                const pathMatch = openedFile[1].match(/The user opened the file (.+?) in the IDE/);
                if (pathMatch) {
                    refs.push({ filePath: pathMatch[1].trim() });
                }
                return '';
            }
            const selection = match.match(/<ide_selection>([\s\S]*?)<\/ide_selection>/);
            if (selection) {
                // Either shape: `from PATH:` then the code, or `from PATH.` when only the lines
                // were sent. The path is lazy so it stops at whichever terminator comes first.
                const selMatch = selection[1].match(
                    /lines (\d+) to (\d+) from (.+?)(?::\s*\n|\.\s)/,
                );
                if (selMatch) {
                    refs.push({
                        filePath: selMatch[3].trim(),
                        startLine: Number(selMatch[1]),
                        endLine: Number(selMatch[2]),
                    });
                }
                return '';
            }
            return '';
        })
        .trim();
    return { text: cleaned, refs };
}

/**
 * A user message with every injected block taken out — what the person actually typed.
 *
 * For the callers that want the words and nothing else: a copy to the clipboard, a queued bubble,
 * a one-line label in a picker.
 *
 * Deliberately a wrapper rather than a second regex: the tag list lives in ONE place, so a block
 * the CLI starts injecting tomorrow disappears from every one of these at once.
 */
export function cleanMessageOnlyText(text: string | undefined | null): string {
    return parseIdeContextTags(text).text;
}

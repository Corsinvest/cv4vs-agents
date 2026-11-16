/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// The `@` file mentions in a sent prompt, so the transcript can draw them as links that open the
// file. Pure, no DOM.

export type MentionSegment =
    | { kind: 'text'; text: string }
    | { kind: 'mention'; text: string; path: string; startLine: number; endLine: number };

// An `@` that starts a token, as the CLI reads it: quoted, or up to the next space, without
// trailing punctuation, so "see @a.cs." does not take the full stop.
const MENTION = /(^|\s)(@"[^"]+"|@\S*[^\s.,;:!?)\]])/g;

// The CLI's own split of a mention: path, optional #Lstart[-end], optional #heading it ignores.
const PARTS = /^([^#]+)(?:#L(\d+)(?:-(\d+))?)?(?:#[^#]*)?$/;

// Package scopes read like paths ("@types/node") but are never files of the project.
const PACKAGE_SCOPE =
    /^(types|babel|eslint|typescript-eslint|angular|vue|nestjs|testing-library|tanstack|radix-ui|reduxjs|emotion|mui|swc|fluentui|lit)\//;

// "server:uri" is an MCP resource; a drive letter ("C:\") is one character, so it is not caught.
const RESOURCE = /^[A-Za-z][\w-]+:/;

function isFilePath(path: string): boolean {
    return (
        /[/.~\\]/.test(path) &&
        !PACKAGE_SCOPE.test(path) &&
        !RESOURCE.test(path) &&
        !path.endsWith(' (agent)')
    );
}

/** The prompt in order, as plain text and file mentions. A mention that does not name a file (a
 *  person, a package, an agent, an MCP resource) stays text. */
export function splitMentions(text: string): MentionSegment[] {
    const out: MentionSegment[] = [];
    let from = 0;
    for (const m of text.matchAll(MENTION)) {
        const raw = m[2];
        const at = (m.index ?? 0) + m[1].length;
        const inner = raw.startsWith('@"') ? raw.slice(2, -1) : raw.slice(1);
        const parts = PARTS.exec(inner);
        const path = parts?.[1] ?? '';
        if (!isFilePath(path)) {
            continue;
        }
        if (at > from) {
            out.push({ kind: 'text', text: text.slice(from, at) });
        }
        const startLine = parts?.[2] ? Number(parts[2]) : 0;
        const endLine = parts?.[3] ? Number(parts[3]) : startLine;
        out.push({ kind: 'mention', text: raw, path, startLine, endLine });
        from = at + raw.length;
    }
    if (from < text.length || out.length === 0) {
        out.push({ kind: 'text', text: text.slice(from) });
    }
    return out;
}

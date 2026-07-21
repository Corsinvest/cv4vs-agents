/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Parses a CLI slash-command envelope (<command-name>/<command-message>/<command-args>)
// out of a user message and returns the display text "/name args" — rendered as a normal
// user bubble by cv-message. A bare "/compact" string (no envelope) needs no parsing and
// flows straight through as its own text.

/** Extract "/name args" from a slash-command envelope, or null when the text isn't one.
 *  Matches <command-name> and <command-args> independently (like the VS Code webview):
 *  the intervening <command-message> is optional, and [\s\S] tolerates the newlines/indent
 *  the CLI inserts between the tags. */
export function renderSlashCommand(text: string): string | null {
    const nameMatch = text.match(/<command-name>([\s\S]*?)<\/command-name>/);
    if (!nameMatch) {
        return null;
    }
    const argsMatch = text.match(/<command-args>([\s\S]*?)<\/command-args>/);
    const name = nameMatch[1].trim();
    const args = argsMatch ? argsMatch[1].trim() : '';
    // command-name may or may not carry the leading slash; normalise to "/name".
    const cmd = name.startsWith('/') ? name : `/${name}`;
    return args ? `${cmd} ${args}` : cmd;
}

/** Extract the inner text of a <local-command-stdout>/<local-command-stderr> envelope —
 *  a slash command's local output (e.g. "Set model to X"), rendered as a slash-result block.
 *  isError = stderr. ANSI SGR codes the CLI leaves in (e.g. dim) are stripped. Returns null
 *  when the text isn't a local-command output. */
export function parseLocalCommandOutput(text: string): { text: string; isError: boolean } | null {
    const stderr = text.match(/<local-command-stderr>([\s\S]*?)<\/local-command-stderr>/);
    if (stderr) {
        return { text: stripAnsi(stderr[1]).trim(), isError: true };
    }
    const stdout = text.match(/<local-command-stdout>([\s\S]*?)<\/local-command-stdout>/);
    if (stdout) {
        return { text: stripAnsi(stdout[1]).trim(), isError: false };
    }
    return null;
}

// eslint-disable-next-line no-control-regex -- ANSI SGR sequences contain the ESC control char.
const ANSI_SGR = /\x1b\[[0-9;]*m/g;
function stripAnsi(s: string): string {
    return s.replace(ANSI_SGR, '');
}

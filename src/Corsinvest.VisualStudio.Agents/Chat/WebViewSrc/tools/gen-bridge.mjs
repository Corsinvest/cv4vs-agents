// SPDX-FileCopyrightText: Copyright Corsinvest Srl
// SPDX-License-Identifier: GPL-3.0-only
//
// Reads ../Host/BridgeMessages.cs and generates ../core/bridge-messages.ts
// so that both sides of the wire share the exact same string values.
//
// The C# file is the single source of truth; never edit the .ts by hand.
// Run via `npm run gen-bridge` or automatically as part of the C# build.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const csFile = resolve(here, '..', '..', 'Host', 'BridgeMessages.cs');
const tsFile = resolve(here, '..', 'core', 'bridge-messages.ts');

const src = readFileSync(csFile, 'utf8');

/**
 * Walk the C# source and pick up:
 * - top-level `public static class FromWebView` / `public static class ToWebView`
 * - their nested `public static class <Domain>` blocks
 * - the `public const string Name = "value";` lines inside each domain
 *
 * Brace counting is enough here — the file has no string literals with `{` or `}`.
 */
function parse() {
    const out = { fromWebView: {}, toWebView: {} };
    let direction = null;
    let domain = null;
    let depth = 0;
    let stack = []; // tracks at which depth `direction`/`domain` were entered
    const lines = src.split('\n');

    const reClassDirection = /public\s+static\s+class\s+(FromWebView|ToWebView)\s*$/;
    const reClassDomain = /public\s+static\s+class\s+([A-Za-z][A-Za-z0-9]*)\s*$/;
    const reConst = /public\s+const\s+string\s+([A-Za-z][A-Za-z0-9]*)\s*=\s*"([^"]+)"\s*;/;

    for (const raw of lines) {
        const line = raw.trim();

        // Direction class
        let m = line.match(reClassDirection);
        if (m) {
            direction = m[1] === 'FromWebView' ? 'fromWebView' : 'toWebView';
            stack.push({ kind: 'direction', depth });
            continue;
        }

        // Domain class — only when we're already inside a direction
        if (direction && (m = line.match(reClassDomain))) {
            domain = m[1].charAt(0).toLowerCase() + m[1].slice(1);
            out[direction][domain] ||= {};
            stack.push({ kind: 'domain', depth });
            continue;
        }

        // Constant
        if (direction && domain && (m = line.match(reConst))) {
            const tsName = m[1].charAt(0).toLowerCase() + m[1].slice(1);
            out[direction][domain][tsName] = m[2];
            continue;
        }

        // Brace tracking — count after recognizing structural lines so
        // class headers don't include their own opening brace yet.
        for (const ch of line) {
            if (ch === '{') {
                depth++;
            } else if (ch === '}') {
                depth--;
                // Pop any scopes that closed at this depth
                while (stack.length > 0 && stack[stack.length - 1].depth >= depth) {
                    const popped = stack.pop();
                    if (popped.kind === 'domain') { domain = null; }
                    if (popped.kind === 'direction') { direction = null; domain = null; }
                }
            }
        }
    }

    return out;
}

function indent(n) {
    return ' '.repeat(n * 4);
}

function emit(tree) {
    const lines = [];
    lines.push('/*');
    lines.push(' * SPDX-FileCopyrightText: Copyright Corsinvest Srl');
    lines.push(' * SPDX-License-Identifier: GPL-3.0-only');
    lines.push(' *');
    lines.push(' * AUTO-GENERATED from Host/BridgeMessages.cs by tools/gen-bridge.mjs.');
    lines.push(' * DO NOT EDIT BY HAND — run `npm run gen-bridge` after changing the C# file.');
    lines.push(' */');
    lines.push('');
    lines.push('export const Msg = {');

    for (const dir of ['fromWebView', 'toWebView']) {
        lines.push(`${indent(1)}${dir}: {`);
        const domains = tree[dir];
        for (const dom of Object.keys(domains)) {
            const consts = domains[dom];
            lines.push(`${indent(2)}${dom}: {`);
            for (const name of Object.keys(consts)) {
                lines.push(`${indent(3)}${name}: '${consts[name]}',`);
            }
            lines.push(`${indent(2)}},`);
        }
        lines.push(`${indent(1)}},`);
    }
    lines.push('} as const;');
    lines.push('');
    return lines.join('\n');
}

const tree = parse();
const output = emit(tree);

mkdirSync(dirname(tsFile), { recursive: true });
writeFileSync(tsFile, output, 'utf8');

const fromCount = Object.values(tree.fromWebView).reduce((a, d) => a + Object.keys(d).length, 0);
const toCount = Object.values(tree.toWebView).reduce((a, d) => a + Object.keys(d).length, 0);
console.log(`gen-bridge: wrote ${tsFile}`);
console.log(`gen-bridge: ${fromCount} fromWebView + ${toCount} toWebView = ${fromCount + toCount} messages`);

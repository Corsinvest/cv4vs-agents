// Node requires an extension on a relative ESM import; the WebView's own modules omit it, because
// esbuild and tsc's "Bundler" resolution both accept that. Append it when the bare specifier does
// not resolve, so `node --test` can load them as they are written.
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

export function resolve(specifier, context, next) {
    if (specifier.startsWith('.') && !/\.[a-z]+$/i.test(specifier)) {
        const guess = new URL(specifier + '.ts', context.parentURL);
        if (existsSync(fileURLToPath(guess))) {
            return next(specifier + '.ts', context);
        }
    }
    return next(specifier, context);
}

// esbuild inlines an .svg import as its text (loader: text). Node has no loader for it, so do the
// same here: any module that imports an icon can then be loaded by a test.
export async function load(url, context, next) {
    if (url.endsWith('.svg')) {
        const svg = await readFile(fileURLToPath(url), 'utf8');
        return { format: 'module', shortCircuit: true, source: `export default ${JSON.stringify(svg)};` };
    }
    return next(url, context);
}

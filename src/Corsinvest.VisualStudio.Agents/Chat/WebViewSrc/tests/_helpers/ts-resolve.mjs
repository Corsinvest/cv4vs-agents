// Node requires an extension on a relative ESM import; the WebView's own modules omit it, because
// esbuild and tsc's "Bundler" resolution both accept that. Append it when the bare specifier does
// not resolve, so `node --test` can load them as they are written.
import { existsSync } from 'node:fs';
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

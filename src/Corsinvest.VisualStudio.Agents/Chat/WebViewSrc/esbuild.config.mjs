// Build script for the TypeScript + Lit WebView source.
//
//   node esbuild.config.mjs           one-shot production build
//   node esbuild.config.mjs --watch   rebuild on file change
//
// Output goes to ./dist (bundle.js + bundle.css). The C# csproj will
// later include dist/** as Content so the VSIX picks it up.

import { context, build } from 'esbuild';
import { mkdir, copyFile, cp, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';

const watch = process.argv.includes('--watch');
const dev = watch || process.argv.includes('--dev');

// Hot-reload without a full MSBuild: the running extension serves the WebView (virtual host
// cv4vs.local) from the WebView2/ folder NEXT TO its loaded assembly. In the experimental VS
// (F5, /rootsuffix Exp) that assembly lives under
// %LOCALAPPDATA%\Microsoft\VisualStudio\<ver>Exp\Extensions\...\WebView2 — NOT bin/<Config>.
// A bare `npm run build` only writes dist/, so a freshly opened chat pane would load the stale
// copy there. Mirror dist/ into every WebView2 folder that already holds a build of this
// extension: (1) bin/<Config>/WebView2 (dev builds), (2) each *Exp Extensions .../WebView2
// (what F5 actually runs). Then closing+reopening a chat pane picks up the new bundle without
// F5. Only existing folders are touched — CI/first build is untouched. Runs after every
// (re)build, including --watch.
async function mirrorTargets() {
    const targets = [];
    for (const config of ['Debug', 'Release']) {
        const dest = path.join('..', '..', 'bin', config, 'WebView2');
        if (existsSync(dest)) { targets.push(dest); }
    }
    // Scan the experimental-VS extension installs for our WebView2 folder.
    const localAppData = process.env.LOCALAPPDATA;
    if (localAppData) {
        const vsRoot = path.join(localAppData, 'Microsoft', 'VisualStudio');
        try {
            for (const inst of await readdir(vsRoot)) {
                if (!inst.endsWith('Exp')) { continue; }
                const wv = path.join(vsRoot, inst, 'Extensions', 'Corsinvest', 'cv4vs Agents');
                if (!existsSync(wv)) { continue; }
                // version subfolder(s) (e.g. 1.0.0) → WebView2
                for (const ver of await readdir(wv)) {
                    const dest = path.join(wv, ver, 'WebView2');
                    if (existsSync(dest)) { targets.push(dest); }
                }
            }
        } catch { /* vsRoot missing: no experimental VS installed */ }
    }
    return targets;
}

async function mirrorDist() {
    if (!existsSync('dist')) { return; }
    for (const dest of await mirrorTargets()) {
        await cp('dist', dest, { recursive: true });
    }
}

// esbuild plugin: mirror after each successful (re)build so --watch also refreshes the
// reload target(s), not just the one-shot build path below.
const mirrorPlugin = {
    name: 'mirror-dist',
    setup(pluginBuild) {
        pluginBuild.onEnd((result) => {
            if (!result.errors.length) { return mirrorDist(); }
        });
    },
};

/** @type {import('esbuild').BuildOptions} */
const config = {
    entryPoints: ['index.ts'],
    bundle: true,
    // IIFE so the bundle works under a `file://` URL inside WebView2 (ESM
    // would trigger strict CORS and be blocked on file: origins).
    format: 'iife',
    target: 'es2020',
    sourcemap: dev ? 'inline' : false,
    minify: !dev,
    outfile: 'dist/bundle.js',
    logLevel: 'info',

    // SVG icons are imported as raw string so the cv-icon component can
    // render them via lit-html unsafeHTML.
    loader: {
        '.svg': 'text',
    },

    // Lit ships ES modules; bundling them is fine.
    // Decorators are emitted as legacy-style by esbuild (compatible with Lit's
    // standard decorators in @customElement / @property).
    tsconfig: 'tsconfig.json',

    plugins: [mirrorPlugin],
};

async function copyStatic() {
    if (!existsSync('dist')) {
        await mkdir('dist', { recursive: true });
    }
    if (existsSync('index.html')) {
        await copyFile('index.html', path.join('dist', 'index.html'));
    }
    // highlight.js themes — loaded as <link> in index.html, toggled via the
    // `disabled` attribute on theme change. Copy them out of node_modules
    // so the VSIX content can pick them up.
    const hljsDir = path.join('dist', 'hljs');
    if (!existsSync(hljsDir)) {
        await mkdir(hljsDir, { recursive: true });
    }
    for (const f of ['vs.min.css', 'vs2015.min.css']) {
        const src = path.join('node_modules', 'highlight.js', 'styles', f);
        if (existsSync(src)) {
            await copyFile(src, path.join(hljsDir, f));
        }
    }
    // diff2html layout CSS (`.d2h-files-diff` flex, `.d2h-file-side-diff`
    // inline-block 50%, etc). Without it side-by-side renders as two stacked
    // full-width blocks instead of a real split view.
    const d2hSrc = path.join('node_modules', 'diff2html', 'bundles', 'css', 'diff2html.min.css');
    if (existsSync(d2hSrc)) {
        await copyFile(d2hSrc, path.join('dist', 'diff2html.min.css'));
    }
    // About-dialog logos. They live in the repo root (single source of
    // truth, also used by README/marketplace).
    const imagesDir = path.join('dist', 'images');
    if (!existsSync(imagesDir)) {
        await mkdir(imagesDir, { recursive: true });
    }
    for (const f of ['plugin-logo.png', 'corsinvest-logo.png']) {
        const src = path.join('..', '..', '..', f);
        if (existsSync(src)) {
            await copyFile(src, path.join(imagesDir, f));
        }
    }
}

if (watch) {
    await copyStatic();
    const ctx = await context(config);
    await ctx.watch();
    console.log('[esbuild] watching for changes…');
} else {
    await copyStatic();
    await build(config);
    console.log('[esbuild] build complete → dist/bundle.js');
}

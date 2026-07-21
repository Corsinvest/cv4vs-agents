/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
//
// driver.mjs — "build + drive" harness for cv4vs-agents (VS 2022 extension).
//
// This extension is NOT headless-launchable: the real UI (the Chat/CLI tool windows)
// lives inside a Visual Studio instance (devenv /rootsuffix Exp) that only a human
// can open. What you CAN do from the command line in this Windows container is:
// (1) build the WebView, (2) build the VSIX, (3) DRIVE the real claude.exe over the
// same stream-json control protocol that ClaudeClient uses.
//
// This driver wraps those three surfaces, already verified by hand.
//
// Usage:
//   node .claude/skills/cv4vs-run/driver.mjs webview     # typecheck + build WebView
//   node .claude/skills/cv4vs-run/driver.mjs vsix        # restore + build VSIX (MSBuild)
//   node .claude/skills/cv4vs-run/driver.mjs probe [sub] # drive claude.exe (default: init)
//   node .claude/skills/cv4vs-run/driver.mjs all         # webview + vsix + probe init
//
// Env:
//   MSBUILD   override the path to MSBuild.exe (default: autodetect via vswhere)

import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
// .claude/skills/cv4vs-run/ → up 3 = repo root.
const repo = resolve(here, '..', '..', '..');
const webviewDir = resolve(repo, 'src', 'Corsinvest.VisualStudio.Agents', 'Chat', 'WebViewSrc');
const probeDir = resolve(repo, 'tools', 'cli-probe');
const sln = resolve(repo, 'cv4vs-agents.sln');

function run(cmd, args, opts = {}) {
    console.log(`\n$ ${cmd} ${args.join(' ')}`);
    // shell:true is needed for npm/node on Windows (they're .cmd). But with shell:true a
    // path with spaces (e.g. "C:\Program Files\...\MSBuild.exe") gets split apart:
    // for those commands we pass shell:false explicitly so spawnSync quotes it itself.
    const r = spawnSync(cmd, args, { stdio: 'inherit', shell: true, ...opts });
    if (r.status !== 0) {
        console.error(`\n✗ command failed (exit ${r.status})`);
        process.exit(r.status ?? 1);
    }
    return r;
}

function findMsbuild() {
    if (process.env.MSBUILD && existsSync(process.env.MSBUILD)) { return process.env.MSBUILD; }
    const vswhere = 'C:\\Program Files (x86)\\Microsoft Visual Studio\\Installer\\vswhere.exe';
    const r = spawnSync(vswhere, [
        '-latest', '-requires', 'Microsoft.Component.MSBuild',
        '-find', 'MSBuild\\**\\Bin\\MSBuild.exe',
    ], { encoding: 'utf8' });
    const path = (r.stdout || '').trim().split(/\r?\n/)[0];
    if (!path || !existsSync(path)) {
        console.error('✗ MSBuild not found. Set env MSBUILD or install VS with the MSBuild component.');
        process.exit(1);
    }
    return path;
}

function webview() {
    console.log('=== WebView: typecheck + build ===');
    run('npm', ['run', 'typecheck'], { cwd: webviewDir });
    run('npm', ['run', 'build'], { cwd: webviewDir });
    const bundle = resolve(webviewDir, 'dist', 'bundle.js');
    console.log(existsSync(bundle) ? `✓ dist/bundle.js produced` : '✗ bundle.js missing');
}

function vsix() {
    console.log('=== VSIX: restore + build (MSBuild) ===');
    const msbuild = findMsbuild();
    // shell:false: the MSBuild.exe path contains spaces ("C:\Program Files\…").
    run(msbuild, [sln, '-t:Restore', '-v:minimal', '-nologo'], { shell: false });
    run(msbuild, [sln, '-t:Build', '-p:Configuration=Debug', '-v:minimal', '-nologo', '-clp:ErrorsOnly;Summary'], { shell: false });
    const out = resolve(repo, 'src', 'Corsinvest.VisualStudio.Agents', 'bin', 'Debug',
        'Corsinvest.VisualStudio.Agents.vsix');
    console.log(existsSync(out) ? `✓ VSIX produced: ${out}` : '✗ VSIX missing');
}

function probe(sub = 'init') {
    console.log(`=== Probe: drive claude.exe (${sub}) ===`);
    // The right entrypoint: without it, init is reduced (no Fable/unavailable_models).
    run('node', ['probe.mjs', sub], {
        cwd: probeDir,
        env: { ...process.env, CLAUDE_CODE_ENTRYPOINT: 'claude-vscode' },
    });
}

const cmd = process.argv[2] || 'all';
const arg = process.argv[3];
switch (cmd) {
    case 'webview': webview(); break;
    case 'vsix': vsix(); break;
    case 'probe': probe(arg); break;
    case 'all': webview(); vsix(); probe('init'); break;
    default:
        console.error(`unknown command: ${cmd}\nuse: webview | vsix | probe [subtype] | all`);
        process.exit(2);
}

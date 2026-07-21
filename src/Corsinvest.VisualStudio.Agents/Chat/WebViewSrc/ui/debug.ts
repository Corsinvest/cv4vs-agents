/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Diagnostic helpers on `window.cv` (always installed): cv.info() logs a
// report to DevTools; cv.dump() returns a string for bug reports.

import { state } from '../core/state';

interface MemoryInfo {
    usedJSHeapSize: number;
    totalJSHeapSize: number;
    jsHeapSizeLimit: number;
}

function fmtMb(bytes: number | undefined): string {
    if (typeof bytes !== 'number') {
        return 'n/a';
    }
    return (bytes / 1048576).toFixed(1) + ' MB';
}

function getMemory(): MemoryInfo | null {
    // performance.memory is Chromium-only (WebView2 has it).
    const perf = performance as Performance & { memory?: MemoryInfo };
    return perf.memory ?? null;
}

interface Snapshot {
    app: {
        theme: string;
        permissionMode: string;
        currentModel: string | null;
        workingDirectory: string;
        isBusy: boolean;
        initialized: boolean;
        logLevel: number;
        perfEnabled: boolean;
    };
    options: {
        previewLines: number;
        diffContextLines: number;
        diffIgnoreWhitespace: boolean;
        showCostAndDuration: boolean;
        showRelativePaths: boolean;
        stickyUserMessages: boolean;
    };
    memory: { used: string; total: string; limit: string };
    dom: {
        toolRows: number;
        messages: number;
        diffPreviews: number;
        attachments: number;
    };
    viewport: { width: number; height: number };
}

function snapshot(): Snapshot {
    const mem = getMemory();
    return {
        app: {
            theme: state.theme,
            permissionMode: state.permissionMode,
            currentModel: state.currentModel,
            workingDirectory: state.workingDirectory,
            isBusy: state.isBusy,
            initialized: state.initialized,
            logLevel: state.ui.logLevel,
            perfEnabled: state.ui.perfEnabled,
        },
        options: {
            previewLines: state.ui.previewLines,
            diffContextLines: state.ui.diffContextLines,
            diffIgnoreWhitespace: state.ui.diffIgnoreWhitespace,
            showCostAndDuration: state.ui.showCostAndDuration,
            showRelativePaths: state.ui.showRelativePaths,
            stickyUserMessages: state.ui.stickyUserMessages,
        },
        memory: {
            used: fmtMb(mem?.usedJSHeapSize),
            total: fmtMb(mem?.totalJSHeapSize),
            limit: fmtMb(mem?.jsHeapSizeLimit),
        },
        dom: {
            toolRows: document.querySelectorAll('cv-tool-row').length,
            messages: document.querySelectorAll('cv-message').length,
            diffPreviews: document.querySelectorAll('cv-diff-preview').length,
            attachments:
                (
                    document.querySelector('cv-prompt') as HTMLElement | null
                )?.shadowRoot?.querySelectorAll('#attachments cv-attach-chip').length ?? 0,
        },
        viewport: {
            width: window.innerWidth,
            height: window.innerHeight,
        },
    };
}

/** Pretty-print the current diagnostic snapshot to the DevTools console. */
function info(): void {
    const s = snapshot();
    /* eslint-disable no-console */
    console.group('%c[cv4vs] info', 'color:#3794ff;font-weight:bold');
    console.log('app', s.app);
    console.log('options', s.options);
    console.log('memory', s.memory);
    console.log('dom', s.dom);
    console.log('viewport', s.viewport);
    console.groupEnd();
    /* eslint-enable no-console */
}

/** Diagnostic report string for bug reports: counts/flags only, no cv-message content. */
function dump(): string {
    const s = snapshot();
    const lines: string[] = [];
    lines.push('=== cv4vs diagnostic ===');
    lines.push('-- app --');
    for (const [k, v] of Object.entries(s.app)) {
        lines.push(`  ${k}: ${v}`);
    }
    lines.push('-- options --');
    for (const [k, v] of Object.entries(s.options)) {
        lines.push(`  ${k}: ${v}`);
    }
    lines.push('-- memory --');
    lines.push(`  used:  ${s.memory.used}`);
    lines.push(`  total: ${s.memory.total}`);
    lines.push(`  limit: ${s.memory.limit}`);
    lines.push('-- dom --');
    for (const [k, v] of Object.entries(s.dom)) {
        lines.push(`  ${k}: ${v}`);
    }
    lines.push('-- viewport --');
    lines.push(`  ${s.viewport.width} x ${s.viewport.height}`);
    return lines.join('\n');
}

/**
 * Install `window.cv` with the public helpers. Called once from init().
 */
export function installDebugApi(): void {
    (window as unknown as { cv: { info: typeof info; dump: typeof dump } }).cv = {
        info,
        dump,
    };
}

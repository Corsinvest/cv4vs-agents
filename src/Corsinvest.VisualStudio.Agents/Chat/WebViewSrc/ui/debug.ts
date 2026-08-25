/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Diagnostic helpers on `window.cv` (always installed): cv.info() logs a
// report to DevTools; cv.dump() returns a string for bug reports.

import { consumedTokens } from '../core/ai-models';
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

function fmtDuration(ms: number): string {
    const s = Math.floor(ms / 1000);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    return h > 0 ? `${h}h ${m}m` : m > 0 ? `${m}m ${s % 60}s` : `${s}s`;
}

function getMemory(): MemoryInfo | null {
    // performance.memory is Chromium-only (WebView2 has it). It measures the JS heap only —
    // the DOM lives in native memory, so the node counts below are the better size signal.
    const perf = performance as Performance & { memory?: MemoryInfo };
    return perf.memory ?? null;
}

/** Walk the page, entering every shadow root. */
function walkAll(visit: (el: Element) => void, root: ParentNode = document.body): void {
    for (const el of root.querySelectorAll('*')) {
        visit(el);
        if (el.shadowRoot) {
            walkAll(visit, el.shadowRoot);
        }
    }
}

/** Elements inside `el`, its own shadow root included. */
function nodesIn(el: Element): number {
    let n = 0;
    const count = (): void => {
        n++;
    };
    if (el.shadowRoot) {
        walkAll(count, el.shadowRoot);
    }
    walkAll(count, el);
    return n;
}

/** What a transcript row is made of, which is what decides its cost. */
type RowKind = 'diff' | 'code' | 'table' | 'image' | 'text';

function rowKind(el: Element): RowKind {
    let kind: RowKind = 'text';
    walkAll((e) => {
        const t = e.localName;
        // First match wins by weight: a diff dwarfs everything else in the same row.
        if (t === 'cv-diff-preview') {
            kind = 'diff';
        } else if (kind !== 'diff' && t === 'table') {
            kind = 'table';
        } else if (kind === 'text' && t === 'pre') {
            kind = 'code';
        } else if (kind === 'text' && t === 'img') {
            kind = 'image';
        }
    }, el);
    return kind;
}

interface DomStats {
    /** Elements in the page, shadow roots included — the number that tracks chat weight.
     *  A plain querySelectorAll stops at each shadow boundary and misses most of them. */
    nodes: number;
    shadowRoots: number;
    toolRows: number;
    messages: number;
    diffPreviews: number;
    attachments: number;
    /** Nodes per top-level row, so a heavy chat can be told from a merely long one. */
    nodesPerRow: number;
    heaviestRow: number;
    /** Nodes and row count per content kind: says WHAT is costing, not just how much. */
    byKind: Record<string, { rows: number; nodes: number; avg: number }>;
}

function domStats(): DomStats {
    let nodes = 0;
    let shadowRoots = 0;
    walkAll((el) => {
        nodes++;
        if (el.shadowRoot) {
            shadowRoots++;
        }
    });

    // Top-level rows only: a nested row is already counted inside its parent.
    const rows: Element[] = [];
    const collect = (root: ParentNode): void => {
        for (const el of root.querySelectorAll('*')) {
            if (el.localName === 'cv-message' || el.localName === 'cv-tool-row') {
                rows.push(el);
                continue;
            }
            if (el.shadowRoot) {
                collect(el.shadowRoot);
            }
        }
    };
    collect(document.body);

    const byKind: DomStats['byKind'] = {};
    let heaviest = 0;
    let rowNodes = 0;
    for (const el of rows) {
        const n = nodesIn(el);
        rowNodes += n;
        heaviest = Math.max(heaviest, n);
        const k = rowKind(el);
        const acc = (byKind[k] ??= { rows: 0, nodes: 0, avg: 0 });
        acc.rows++;
        acc.nodes += n;
    }
    for (const acc of Object.values(byKind)) {
        acc.avg = +(acc.nodes / acc.rows).toFixed(1);
    }

    return {
        nodes,
        shadowRoots,
        toolRows: rows.filter((r) => r.localName === 'cv-tool-row').length,
        messages: rows.filter((r) => r.localName === 'cv-message').length,
        diffPreviews: document.querySelectorAll('cv-diff-preview').length,
        attachments:
            (
                document.querySelector('cv-prompt') as HTMLElement | null
            )?.shadowRoot?.querySelectorAll('#attachments cv-attach-chip').length ?? 0,
        nodesPerRow: rows.length ? +(rowNodes / rows.length).toFixed(1) : 0,
        heaviestRow: heaviest,
        byKind,
    };
}

interface Snapshot {
    app: {
        version: string;
        theme: string;
        workingDirectory: string;
        sessionId: string;
        initialized: boolean;
        inDev: boolean;
        logLevel: number;
        perfEnabled: boolean;
        /** How long this page has been up. A renderer open since this morning reads its memory
         *  numbers differently from one opened a minute ago. */
        uptime: string;
    };
    /** What the CLI is running with — the first question on any "it answered wrong" report. */
    cli: {
        model: string | null;
        permissionMode: string;
        effort: string;
        thinking: boolean;
        ultracode: boolean;
        fastMode: boolean;
        isBusy: boolean;
        status: string;
        subagents: number;
    };
    /** Token usage against the window: a chat that compacted on its own explains itself here. */
    context: {
        window: number;
        used: number;
        pctOfWindow: string;
        cacheRead: number;
        cacheCreation: number;
    };
    options: {
        previewLines: number;
        showCostAndDuration: boolean;
        showRelativePaths: boolean;
        stickyUserMessages: boolean;
        showInlineToolErrors: boolean;
        chatFontSize: number;
    };
    memory: { used: string; total: string; limit: string };
    dom: DomStats;
    history: { hasMore: boolean; loadingOlder: boolean; oldestOffset: number };
    ide: { contextEnabled: boolean; hasContext: boolean };
    viewport: { width: number; height: number; dpr: number };
}

function snapshot(): Snapshot {
    const mem = getMemory();
    const u = state.contextUsage;
    // The gauge's own sum, so the dialog and the ring can never disagree — note it leaves out
    // outputTokens, which are not in the window yet when the reading is taken.
    const used = u ? consumedTokens(u) : 0;
    const win = state.contextWindow;
    return {
        app: {
            version: state.ui.appVersion || '(unknown)',
            theme: state.theme,
            workingDirectory: state.workingDirectory,
            sessionId: state.currentSessionId || '(none)',
            initialized: state.initialized,
            inDev: state.inDev,
            logLevel: state.ui.logLevel,
            perfEnabled: state.ui.perfEnabled,
            uptime: fmtDuration(performance.now()),
        },
        cli: {
            model: state.currentModel,
            permissionMode: state.permissionMode,
            effort: state.effortLevel,
            thinking: state.thinkingEnabled,
            ultracode: state.ultracodeEnabled,
            fastMode: state.fastMode,
            isBusy: state.isBusy,
            status: state.status || '(idle)',
            subagents: state.subagentTasks.length,
        },
        context: {
            window: win,
            used,
            pctOfWindow: win ? `${((used / win) * 100).toFixed(1)}%` : 'n/a',
            cacheRead: u?.cacheReadTokens ?? 0,
            cacheCreation: u?.cacheCreationTokens ?? 0,
        },
        options: {
            previewLines: state.ui.previewLines,
            showCostAndDuration: state.ui.showCostAndDuration,
            showRelativePaths: state.ui.showRelativePaths,
            stickyUserMessages: state.ui.stickyUserMessages,
            showInlineToolErrors: state.ui.showInlineToolErrors,
            chatFontSize: state.ui.chatFontSize,
        },
        memory: {
            used: fmtMb(mem?.usedJSHeapSize),
            total: fmtMb(mem?.totalJSHeapSize),
            limit: fmtMb(mem?.jsHeapSizeLimit),
        },
        dom: domStats(),
        history: {
            hasMore: state.hasMoreHistory,
            loadingOlder: state.loadingOlder,
            oldestOffset: state.oldestLoadedOffset,
        },
        ide: {
            contextEnabled: state.ideContextEnabled,
            hasContext: state.ideContext !== null,
        },
        viewport: {
            width: window.innerWidth,
            height: window.innerHeight,
            dpr: window.devicePixelRatio,
        },
    };
}

/** Sections in the order they answer questions: what is running, then what it is chewing on,
 *  then how heavy the page got. Shared by every renderer so they never drift apart. */
function sections(s: Snapshot): [string, Record<string, unknown>][] {
    // byKind is a table of its own, rendered separately by each caller.
    const dom: Record<string, unknown> = { ...s.dom };
    delete dom.byKind;
    return [
        ['app', s.app],
        ['cli', s.cli],
        ['context', s.context],
        ['dom', dom],
        ['memory', s.memory],
        ['history', s.history],
        ['ide', s.ide],
        ['options', s.options],
        ['viewport', s.viewport],
    ];
}

/** Kinds sorted by weight, with each one's share of the page. */
function kindRows(
    s: Snapshot,
): { kind: string; rows: number; nodes: number; avg: number; share: string }[] {
    return Object.entries(s.dom.byKind)
        .sort((a, b) => b[1].nodes - a[1].nodes)
        .map(([kind, v]) => ({
            kind,
            rows: v.rows,
            nodes: v.nodes,
            avg: v.avg,
            share: s.dom.nodes ? `${((v.nodes / s.dom.nodes) * 100).toFixed(0)}%` : '0%',
        }));
}

/** Pretty-print the current diagnostic snapshot to the DevTools console. */
function info(): void {
    const s = snapshot();
    console.group('%c[cv4vs] info', 'color:#3794ff;font-weight:bold');
    for (const [title, obj] of sections(s)) {
        console.log(title, obj);
    }
    // A table beats a nested object here: the whole point is comparing kinds by weight.
    console.table(kindRows(s));
    console.groupEnd();
}

/** Diagnostic report as plain text — for reading in the console, where markdown pipes and
 *  dashes are noise. Counts and flags only, never cv-message content. */
function dump(): string {
    const s = snapshot();
    const lines = ['=== cv4vs diagnostic ==='];
    for (const [title, obj] of sections(s)) {
        lines.push(`-- ${title} --`);
        for (const [k, v] of Object.entries(obj)) {
            lines.push(`  ${k}: ${v}`);
        }
    }
    lines.push('-- dom by kind --');
    for (const r of kindRows(s)) {
        lines.push(`  ${r.kind}: ${r.rows} rows, ${r.nodes} nodes, avg ${r.avg} (${r.share})`);
    }
    return lines.join('\n');
}

/** Diagnostic report as markdown — for pasting into an issue, where the tables render and a
 *  `<details>` keeps it from burying the actual report. */
function dumpMarkdown(): string {
    const s = snapshot();
    const out = ['<details>', '<summary>cv4vs diagnostic</summary>', ''];
    for (const [title, obj] of sections(s)) {
        out.push(`**${title}**`, '', '| | |', '|---|---|');
        for (const [k, v] of Object.entries(obj)) {
            out.push(`| ${k} | \`${v}\` |`);
        }
        out.push('');
    }
    const kinds = kindRows(s);
    if (kinds.length) {
        out.push(
            '**dom by kind**',
            '',
            '| kind | rows | nodes | avg | share |',
            '|---|---|---|---|---|',
        );
        for (const r of kinds) {
            out.push(`| ${r.kind} | ${r.rows} | ${r.nodes} | ${r.avg} | ${r.share} |`);
        }
        out.push('');
    }
    out.push('</details>');
    return out.join('\n');
}

/**
 * Install `window.cv` with the public helpers. Called once from init().
 *
 * `info` reads in the console, `dump` is the same report as plain text, `markdown` is the shape
 * an issue renders. No copy helper: the pane's Info dialog already has a Copy button, and it is
 * `dump()` that it shows — see ChatPaneControl.ExtraSessionSections, which calls it BY NAME
 * through ExecuteScriptAsync. Renaming these silently empties that dialog's last section.
 */
export function installDebugApi(): void {
    (
        window as unknown as {
            cv: {
                info: typeof info;
                dump: typeof dump;
                markdown: typeof dumpMarkdown;
            };
        }
    ).cv = {
        info,
        dump,
        markdown: dumpMarkdown,
    };
}

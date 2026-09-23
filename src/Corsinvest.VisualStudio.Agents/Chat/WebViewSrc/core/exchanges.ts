// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import type { UiEntry } from './types';

/**
 * Group a transcript into exchanges: each user message opens one, and whatever precedes the first
 * user message (a history page boundary) gets its own leading group.
 *
 * A message still in the queue is the exception — it heads no turn, since the CLI has not been
 * given it. Opening an exchange for it would end the running turn's <section> early, and the
 * sticky user bubble pins only within its own section (.cv-exchange in chat.css): that turn's
 * header would come unstuck while its reply is still arriving. It opens its own group once sent.
 *
 * Pure and derived — cv-app calls this from a memoised getter instead of holding the groups as
 * state, so the groups can never drift from the entries they are built from.
 */
export function buildGroups(
    entries: readonly UiEntry[],
    queued?: ReadonlySet<string>,
): UiEntry[][] {
    const isQueued = (uuid?: string): boolean => !!uuid && !!queued?.has(uuid);
    const opensExchange = (e: UiEntry): boolean =>
        e.kind === 'text' && e.role === 'user' && !isQueued(e.uuid);

    const groups: UiEntry[][] = [];
    let current: UiEntry[] = [];
    for (const e of entries) {
        if (opensExchange(e)) {
            if (current.length) {
                groups.push(current);
            }
            current = [e];
        } else {
            current.push(e);
        }
    }
    if (current.length) {
        groups.push(current);
    }
    return groups;
}

/**
 * Tool rows that stay on screen when tool calls are hidden: each is something the user took part in
 * or that is addressed to them, not the work in between — their answers (AskUserQuestion), the plan
 * they approved or sent back (ExitPlanMode), the task list (TodoWrite, and the TaskCreate/TaskUpdate
 * that replaced it), and prose the model sends them (Brief, still SendUserMessage on the wire).
 */
const KEPT_WHEN_TOOL_CALLS_HIDDEN: ReadonlySet<string> = new Set([
    'AskUserQuestion',
    'ExitPlanMode',
    'TodoWrite',
    'TaskCreate',
    'TaskUpdate',
    'Brief',
    'SendUserMessage',
]);

/**
 * Whether the "hide tool calls" filter hides this entry. Only tool rows ever are, and not the kinds
 * kept above, nor the call awaiting the user's approval: its row is where what they are approving —
 * the edit, the command — is spelled out.
 */
export function isHiddenToolCall(e: UiEntry, pendingToolUseId?: string | null): boolean {
    return (
        e.kind === 'tool' &&
        !KEPT_WHEN_TOOL_CALLS_HIDDEN.has(e.data.name) &&
        e.toolUseId !== pendingToolUseId
    );
}

/**
 * Whether Focus folds this entry into its run: the tool rows the hide filter would take, plus
 * thinking. Everything else — prose, the tools the user took part in, the call awaiting their
 * approval — stays out and ends the run it interrupts.
 */
export function isFolded(e: UiEntry, pendingToolUseId?: string | null): boolean {
    return isHiddenToolCall(e, pendingToolUseId) || (e.kind === 'text' && e.role === 'thinking');
}

/** One run of consecutive folded entries in a response: [start, end) and what it held. */
export interface FoldRun {
    /** Stable across re-renders: the first entry's tool-use id, or its entry id for thinking. */
    key: string;
    start: number;
    end: number;
    toolCount: number;
    errorCount: number;
    thinkingMs: number;
    thinkingStreaming: boolean;
    runningTool: string | null;
}

const foldKey = (e: UiEntry): string => (e.kind === 'tool' ? `t${e.toolUseId}` : `e${e.id}`);

/** The runs of a response, keyed by the index of their first entry — the slot the fold row rides in. */
export function buildFoldRuns(
    response: readonly UiEntry[],
    pendingToolUseId?: string | null,
): Map<number, FoldRun> {
    const runs = new Map<number, FoldRun>();
    let run: FoldRun | null = null;
    for (let i = 0; i < response.length; i++) {
        const e = response[i];
        if (!isFolded(e, pendingToolUseId)) {
            run = null;
            continue;
        }
        if (!run) {
            run = {
                key: foldKey(e),
                start: i,
                end: i + 1,
                toolCount: 0,
                errorCount: 0,
                thinkingMs: 0,
                thinkingStreaming: false,
                runningTool: null,
            };
            runs.set(i, run);
        }
        run.end = i + 1;
        if (e.kind === 'tool') {
            run.toolCount++;
            if (e.status === 'error') {
                run.errorCount++;
            }
            if (e.status === 'pending') {
                run.runningTool = e.data.name;
            }
        } else if (e.role === 'thinking') {
            run.thinkingMs += e.durationMs ?? 0;
            run.thinkingStreaming ||= !!e.streaming;
        }
    }
    return runs;
}

/** The fold row's settled text — the same wording as the VS Code extension's Focus view. */
export function foldLabel(run: FoldRun): string {
    if (run.toolCount > 0) {
        const calls = `${run.toolCount} tool call${run.toolCount === 1 ? '' : 's'}`;
        return run.errorCount > 0 ? `${calls} · ${run.errorCount} failed` : calls;
    }
    if (run.thinkingStreaming) {
        return 'Thinking…';
    }
    return run.thinkingMs > 0
        ? `Thought for ${Math.max(1, Math.round(run.thinkingMs / 1000))}s`
        : 'Thinking';
}

/** The caller decides whether the run is live at all (the last run of the running turn). */
export function foldLiveLabel(run: FoldRun): string | null {
    return run.runningTool
        ? `Running ${run.runningTool}…`
        : run.thinkingStreaming
          ? 'Thinking…'
          : null;
}

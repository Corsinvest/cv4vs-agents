// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    buildGroups,
    buildFoldRuns,
    foldLabel,
    foldLiveLabel,
    isFolded,
    isHiddenToolCall,
} from '../core/exchanges.ts';
import type { UiAssistantEntry, UiThinkingEntry, UiToolEntry, UiUserEntry } from '../core/types';

const user = (id: number, uuid?: string): UiUserEntry => ({
    kind: 'text',
    id,
    role: 'user',
    text: `u${id}`,
    uuid,
});
const bot = (id: number): UiAssistantEntry => ({
    kind: 'text',
    id,
    role: 'assistant',
    text: `a${id}`,
});

test('every user message opens a new exchange', () => {
    const groups = buildGroups([user(1), bot(2), user(3), bot(4)]);

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [3, 4],
    );
});

test('entries before the first user go into a leading group', () => {
    const groups = buildGroups([bot(1), user(2), bot(3)]);

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [2, 3],
    );
});

test('an empty list produces zero groups', () => {
    assert.deepEqual(buildGroups([]), []);
});

test('a message still queued does not open an exchange', () => {
    // Turn 1 is still answering (bot(4) arrives later) when the user types the second prompt:
    // while it stays queued it must live in the running turn's group, or its <section> would end
    // there and the running turn's header would unstick halfway through the answer.
    const groups = buildGroups([user(1), bot(2), user(3, 'q'), bot(4)], new Set(['q']));

    assert.equal(groups.length, 1);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2, 3, 4],
    );
});

test('the same message opens the exchange as soon as it leaves the queue', () => {
    const entries = [user(1), bot(2), user(3, 'q')];

    assert.equal(buildGroups(entries, new Set(['q'])).length, 1);
    assert.equal(buildGroups(entries, new Set()).length, 2);
});

test('the queue holds back only its own uuid, not every user message', () => {
    // 'b' is queued, 'a' is not: 'a' opens its own exchange, 'b' stays in the group it finds.
    const groups = buildGroups([user(1, 'z'), user(2, 'b'), user(3, 'a')], new Set(['b']));

    assert.equal(groups.length, 2);
    assert.deepEqual(
        groups[0].map((e) => e.id),
        [1, 2],
    );
    assert.deepEqual(
        groups[1].map((e) => e.id),
        [3],
    );
});

const tool = (id: number, name: string): UiToolEntry => ({
    kind: 'tool',
    id,
    toolUseId: `toolu_${id}`,
    data: { id: `toolu_${id}`, name },
    status: 'done',
    result: '',
    fullLineCount: 0,
    elapsedSec: 0,
});

test('hidden tool calls take the work in between: commands, reads, searches, MCP, sub-agents', () => {
    const work = [
        'Bash',
        'PowerShell',
        'Grep',
        'Read',
        'Edit',
        'mcp__vs__build_solution',
        'Agent',
        'TaskList',
    ];
    for (const name of work) {
        assert.equal(isHiddenToolCall(tool(1, name)), true, name);
    }
});

test('what the user took part in stays: answers, plans, the task list, messages to them', () => {
    const kept = [
        'AskUserQuestion',
        'ExitPlanMode',
        'TodoWrite',
        'TaskCreate',
        'TaskUpdate',
        'Brief',
        'SendUserMessage',
    ];
    for (const name of kept) {
        assert.equal(isHiddenToolCall(tool(1, name)), false, name);
    }
});

test('the call waiting for approval stays visible, and only that one', () => {
    const bash = tool(7, 'Bash');

    assert.equal(isHiddenToolCall(bash, 'toolu_7'), false);
    assert.equal(isHiddenToolCall(bash, 'toolu_8'), true);
    assert.equal(isHiddenToolCall(bash, null), true);
});

test('only tool rows are ever hidden', () => {
    const thinking: UiThinkingEntry = { kind: 'text', id: 3, role: 'thinking', text: 't' };

    for (const e of [user(1), bot(2), thinking]) {
        assert.equal(isHiddenToolCall(e), false);
    }
});

const think = (id: number, durationMs = 0, streaming = false): UiThinkingEntry => ({
    kind: 'text',
    id,
    role: 'thinking',
    text: '',
    durationMs,
    streaming,
});
const toolAs = (id: number, name: string, status: UiToolEntry['status']): UiToolEntry => ({
    ...tool(id, name),
    status,
});

test('focus folds hidden tool calls and thinking, never prose or kept tools', () => {
    assert.equal(isFolded(tool(1, 'Bash')), true);
    assert.equal(isFolded(think(2)), true);
    assert.equal(isFolded(bot(3)), false);
    assert.equal(isFolded(user(4)), false);
    assert.equal(isFolded(tool(5, 'AskUserQuestion')), false);
    assert.equal(isFolded(tool(6, 'Bash'), 'toolu_6'), false);
});

test('each run of consecutive folded entries is one fold, split by prose', () => {
    const runs = buildFoldRuns([
        bot(1),
        tool(2, 'Read'),
        think(3, 1500),
        tool(4, 'Edit'),
        bot(5),
        tool(6, 'Bash'),
    ]);

    assert.deepEqual([...runs.keys()], [1, 5]);
    const first = runs.get(1)!;
    assert.equal(first.start, 1);
    assert.equal(first.end, 4);
    assert.equal(first.toolCount, 2);
    assert.equal(first.thinkingMs, 1500);
    assert.equal(first.key, 'ttoolu_2');
    assert.equal(runs.get(5)!.toolCount, 1);
});

test('a kept tool breaks a run like prose does', () => {
    const runs = buildFoldRuns([tool(1, 'Read'), tool(2, 'AskUserQuestion'), tool(3, 'Edit')]);

    assert.deepEqual([...runs.keys()], [0, 2]);
});

test('the call awaiting permission breaks a run while it waits', () => {
    const response = [tool(1, 'Read'), tool(2, 'Bash'), tool(3, 'Edit')];

    assert.deepEqual([...buildFoldRuns(response, 'toolu_2').keys()], [0, 2]);
    assert.deepEqual([...buildFoldRuns(response, null).keys()], [0]);
});

test('a run keyed on a thinking block uses the entry id', () => {
    const runs = buildFoldRuns([think(7), tool(8, 'Read')]);

    assert.equal(runs.get(0)!.key, 'e7');
});

test('fold label counts calls and failures', () => {
    const runs = buildFoldRuns([tool(1, 'Read'), toolAs(2, 'Bash', 'error'), tool(3, 'Edit')]);

    assert.equal(foldLabel(runs.get(0)!), '3 tool calls · 1 failed');
    assert.equal(foldLabel(buildFoldRuns([tool(1, 'Read')]).get(0)!), '1 tool call');
});

test('a thinking-only fold reports how long it thought', () => {
    assert.equal(foldLabel(buildFoldRuns([think(1, 7600)]).get(0)!), 'Thought for 8s');
    assert.equal(foldLabel(buildFoldRuns([think(1, 200)]).get(0)!), 'Thought for 1s');
    assert.equal(foldLabel(buildFoldRuns([think(1, 0)]).get(0)!), 'Thinking');
    assert.equal(foldLabel(buildFoldRuns([think(1, 0, true)]).get(0)!), 'Thinking…');
});

test('live label names the running tool, else streaming thinking, else nothing', () => {
    const running = buildFoldRuns([tool(1, 'Read'), toolAs(2, 'Bash', 'pending')]).get(0)!;
    const thinking = buildFoldRuns([tool(1, 'Read'), think(2, 0, true)]).get(0)!;
    const settled = buildFoldRuns([tool(1, 'Read')]).get(0)!;

    assert.equal(foldLiveLabel(running), 'Running Bash…');
    assert.equal(foldLiveLabel(thinking), 'Thinking…');
    assert.equal(foldLiveLabel(settled), null);
});

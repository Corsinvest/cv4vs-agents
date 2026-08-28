// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Transcript } from '../core/transcript.ts';
import type { UiAssistantEntry, UiEntry, UiToolEntry, UiUserEntry } from '../core/types';

function userEntry(id: number, text = 't'): UiUserEntry {
    return { kind: 'text', id, role: 'user', text };
}

function toolEntry(id: number, toolUseId: string): UiToolEntry {
    return {
        kind: 'tool',
        id,
        toolUseId,
        data: { id: toolUseId, name: 'Agent', input: {} },
        status: 'pending',
        result: '',
        fullLineCount: 0,
        elapsedSec: 0,
    };
}

/** What cv-app uses to decide two children are the same row. */
const childKey = (e: UiEntry): string => (e.kind === 'tool' ? e.toolUseId : `t${e.id}`);

test('append: new array, existing entries keep their reference', () => {
    const t = new Transcript();
    const a = userEntry(1);
    t.append(a);
    const first = t.entries;

    t.append(userEntry(2));

    assert.notEqual(t.entries, first, 'entries must be a new array');
    assert.equal(t.entries[0], a, 'the existing entry keeps its reference');
    assert.equal(t.entries.length, 2);
});

test('find: returns the entry by id, null when absent', () => {
    const t = new Transcript();
    const a = userEntry(7);
    t.append(a);

    assert.equal(t.find(7), a);
    assert.equal(t.find(99), null);
});

test('clear: empties entries and index', () => {
    const t = new Transcript();
    t.append(userEntry(1));

    t.clear();

    assert.equal(t.entries.length, 0);
    assert.equal(t.find(1), null, 'the index must be emptied along with the entries');
});

test('update: replaces the entry, the other branches stay untouched', () => {
    const t = new Transcript();
    const a = userEntry(1, 'a');
    const b = userEntry(2, 'b');
    t.append(a);
    t.append(b);

    const ok = t.update<UiUserEntry>(1, (e) => ({ ...e, text: 'new' }));

    assert.equal(ok, true);
    assert.notEqual(t.entries[0], a, 'the updated entry has a new reference');
    assert.equal((t.entries[0] as UiUserEntry).text, 'new');
    assert.equal(t.entries[1], b, 'untouched branches keep their reference');
});

test('update: a missing id returns false without throwing', () => {
    const t = new Transcript();

    assert.equal(
        t.update(42, (e) => e),
        false,
    );
});

test('appendText: concatenates and recreates only that entry', () => {
    const t = new Transcript();
    const a = userEntry(1, 'ab');
    const b = userEntry(2, 'x');
    t.append(a);
    t.append(b);

    assert.equal(t.appendText(1, 'cd'), true);
    assert.equal((t.entries[0] as UiUserEntry).text, 'abcd');
    assert.equal(t.entries[1], b, 'the other branches stay untouched');
});

test('appendText: a missing id returns false', () => {
    const t = new Transcript();

    assert.equal(t.appendText(9, 'x'), false);
});

test('appendChild: new parent, children and items; siblings untouched', () => {
    const t = new Transcript();
    const parent = toolEntry(1, 'tool-1');
    const sibling = userEntry(2);
    t.append(parent);
    t.append(sibling);

    const ok = t.appendChild('tool-1', userEntry(3), childKey);

    assert.equal(ok, true);
    const p = t.entries[0] as UiToolEntry;
    assert.notEqual(p, parent, 'the parent has a new reference');
    assert.notEqual(p.children, parent.children, 'and so does the children block');
    assert.equal(p.children?.items.length, 1);
    assert.equal(t.entries[1], sibling, 'the sibling keeps its reference');
});

// If the ring dropped a child by mutating items in place, the parent's children block would stay
// identical by ===: cv-tool-row would not be updated and its ChildParts would point at removed
// nodes.
test('appendChild: the ring dropping a child still recreates the children block', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    for (let i = 2; i <= 4; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }
    const before = (t.entries[0] as UiToolEntry).children;

    t.appendChild('tool-1', userEntry(5), childKey);

    const after = (t.entries[0] as UiToolEntry).children;
    assert.notEqual(after, before, 'the children block must have a new identity');
    assert.notEqual(after?.items, before?.items, 'and so must items');
    assert.equal(after?.items.length, 3, 'the last 3 are kept');
    assert.equal(after?.hasMore, true);
    assert.deepEqual(
        after?.items.map((e) => e.id),
        [3, 4, 5],
    );
});

test('appendChild: showAll keeps everything and upserts instead of duplicating', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'tool-1'));
    t.update<UiToolEntry>(1, (e) => ({
        ...e,
        children: { items: [], hasMore: false, showAll: true },
    }));
    for (let i = 2; i <= 5; i++) {
        t.appendChild('tool-1', userEntry(i), childKey);
    }

    // re-emitting the same child: updates in place, does not duplicate
    t.appendChild('tool-1', userEntry(3, 'updated'), childKey);

    const p = t.entries[0] as UiToolEntry;
    assert.equal(
        p.children?.items.length,
        4,
        'showAll keeps everything, upsert does not duplicate',
    );
    assert.equal((p.children?.items[1] as UiUserEntry).text, 'updated');
});

test('appendChild: a missing parent returns false', () => {
    const t = new Transcript();

    assert.equal(t.appendChild('nope', userEntry(1), childKey), false);
});

test('replaceAll: replaces the tree and rebuilds the index from scratch', () => {
    const t = new Transcript();
    t.append(userEntry(1));
    t.append(toolEntry(2, 'tool-2'));
    t.appendChild('tool-2', userEntry(3), childKey);

    t.replaceAll([userEntry(10), toolEntry(11, 'new')]);

    assert.equal(t.entries.length, 2);
    assert.equal(t.find(1), null, 'the old ids must not survive');
    assert.equal(t.find(3), null, 'nor the nested ones');
    assert.equal(t.find(10)?.id, 10);
});

test('replaceAll: indexes nested children that are already there', () => {
    const parent = toolEntry(1, 'p');
    parent.children = { items: [userEntry(2)], hasMore: false, showAll: false };
    const t = new Transcript();

    t.replaceAll([parent]);

    assert.equal(t.find(2)?.id, 2, 'children coming from history must be indexed');
    assert.equal(
        t.update<UiUserEntry>(2, (e) => ({ ...e, text: 'x' })),
        true,
    );
});

test('prepend: puts entries at the head and keeps the index consistent', () => {
    const t = new Transcript();
    t.append(userEntry(5));

    t.prepend([userEntry(1), userEntry(2)]);

    assert.deepEqual(
        t.entries.map((e) => e.id),
        [1, 2, 5],
    );
    assert.equal(t.find(1)?.id, 1);
    assert.equal(t.find(5)?.id, 5, 'pre-existing ids stay resolvable too');
});

test('updateMany: updates N entries in one go', () => {
    const t = new Transcript();
    t.append(userEntry(1, 'a'));
    t.append(userEntry(2, 'b'));
    const before = t.entries;

    t.updateMany([1, 2], (e) => ('text' in e ? { ...e, text: e.text + '!' } : e));

    assert.notEqual(t.entries, before);
    assert.equal((t.entries[0] as UiUserEntry).text, 'a!');
    assert.equal((t.entries[1] as UiUserEntry).text, 'b!');
});

test('findToolByAgentId: finds the Agent row that launched the sub-agent', () => {
    const t = new Transcript();
    const row = toolEntry(1, 'tool-1');
    row.agentId = 'agent-42';
    t.append(row);

    assert.equal(t.findToolByAgentId('agent-42')?.id, 1);
    assert.equal(t.findToolByAgentId('nope'), null);
});

// "Show all" on an Agent still running: the fetch replaces children.items with the whole history,
// but those children never go through appendChild. Without a reindex they stay out of the index,
// the live event arriving right after does not find them and the update hits the wrong branch.
test('update: replacing children.items indexes the children arriving from outside', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'agent'));
    t.appendChild('agent', userEntry(2), childKey);

    // the "Show all" fetch: replaces the 3 kept ones with the full history
    t.update<UiToolEntry>(1, (e) => ({
        ...e,
        children: {
            items: [userEntry(10), userEntry(11), toolEntry(12, 'inner')],
            hasMore: false,
            showAll: true,
        },
    }));

    // the agent is still working: an event arrives for a child of the replaced list
    assert.equal(t.find(11)?.id, 11, 'the replaced children must be reachable');
    assert.equal(
        t.update<UiUserEntry>(11, (e) => ({ ...e, text: 'updated' })),
        true,
        'update must reach a child that came in with the replacement',
    );
    assert.equal(t.findTool('inner')?.id, 12);
    assert.equal(t.find(2), null, 'the replaced child must no longer resolve');
});

test('nested appendChild: the index resolves a child of a child', () => {
    const t = new Transcript();
    t.append(toolEntry(1, 'outer'));
    t.appendChild('outer', toolEntry(2, 'inner'), childKey);
    t.appendChild('inner', userEntry(3, 'deep'), childKey);

    assert.equal(t.find(3)?.id, 3);
    assert.equal(t.findTool('inner')?.id, 2);
    assert.equal(
        t.update<UiUserEntry>(3, (e) => ({ ...e, text: 'x' })),
        true,
        'update must reach a second-level child',
    );
});

function assistantEntry(id: number, uuid?: string): UiAssistantEntry {
    return { kind: 'text', id, role: 'assistant', text: 'a', uuid };
}

test('removeByUuid: removes only the named entries', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(assistantEntry(2, 'u2'));
    t.append(assistantEntry(3, 'u3'));

    assert.equal(t.removeByUuid(['u2']), 1);
    assert.deepEqual(
        t.entries.map((e) => e.id),
        [1, 3],
    );
});

test('removeByUuid: unknown uuids are a no-op, and repeating it changes nothing', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    const before = t.entries;

    assert.equal(t.removeByUuid(['never-seen']), 0, 'an unknown uuid removes nothing');
    assert.equal(t.entries, before, 'with no removals the array is not recreated');

    assert.equal(t.removeByUuid(['u1']), 1);
    assert.equal(t.removeByUuid(['u1']), 0, 'the second time there is nothing left to remove');
});

test('removeByUuid: an entry without a uuid is never touched', () => {
    const t = new Transcript();
    t.append(assistantEntry(1));
    t.append(userEntry(2));

    assert.equal(t.removeByUuid(['u1', 'u2']), 0);
    assert.equal(t.entries.length, 2);
});

test('removeByUuid: removing an entry does not detach the others from the index', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'outer'));
    t.appendChild('outer', userEntry(3), childKey);

    assert.equal(t.removeByUuid(['u1']), 1);
    // The removal rebuilds the index: doing it halfway would leave the children of the SURVIVING
    // entry indexed on the old path, and an event for them would land elsewhere.
    assert.equal(t.find(3)?.id, 3, 'the child of a surviving entry must still resolve');
    assert.equal(t.findTool('outer')?.id, 2);
});

test('removeByUuid: onRemoved also receives the children of the removed entry', () => {
    const t = new Transcript();
    const seen: number[][] = [];
    t.onRemoved = (ids) => seen.push([...ids]);

    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'outer'));
    t.appendChild('outer', userEntry(3), childKey);

    t.removeByUuid(['u1']);
    assert.deepEqual(seen, [[1]], 'an entry without children reports only itself');

    // Whoever mapped the id of a nested row is left dangling just like whoever mapped the parent:
    // the event must name the whole subtree, not just the root.
    seen.length = 0;
    const t2 = new Transcript();
    t2.onRemoved = (ids) => seen.push([...ids]);
    t2.append({ ...toolEntry(10, 'root'), uuid: 'ur' } as unknown as UiEntry);
    t2.appendChild('root', toolEntry(11, 'mid'), childKey);
    t2.appendChild('mid', userEntry(12), childKey);

    t2.removeByUuid(['ur']);
    assert.deepEqual(seen, [[10, 11, 12]]);
});

test('removeByUuid: with no removals onRemoved does not fire', () => {
    const t = new Transcript();
    let calls = 0;
    t.onRemoved = () => calls++;
    t.append(assistantEntry(1, 'u1'));

    t.removeByUuid(['other']);
    assert.equal(calls, 0);
});

test('removeByUuid: reaches an entry nested inside a sub-agent too', () => {
    const t = new Transcript();
    const removed: number[][] = [];
    t.onRemoved = (ids) => removed.push([...ids]);

    t.append(toolEntry(1, 'agent'));
    t.appendChild('agent', assistantEntry(2, 'inside'), childKey);
    t.appendChild('agent', userEntry(3), childKey);

    assert.equal(t.removeByUuid(['inside']), 1);
    assert.equal(t.find(2), null, 'the nested entry must disappear');
    assert.equal(t.find(3)?.id, 3, 'the siblings stay');
    assert.equal(t.findTool('agent')?.id, 1, 'the parent stays');
    assert.deepEqual(removed, [[2]]);
});

test('removeByUuid: a branch with no removals keeps its identity', () => {
    const t = new Transcript();
    t.append(assistantEntry(1, 'u1'));
    t.append(toolEntry(2, 'untouched'));
    t.appendChild('untouched', userEntry(3), childKey);
    const before = t.entries[1];

    t.removeByUuid(['u1']);

    // If the prune recreated the untouched branches too, Lit would re-render the sub-agent's whole
    // subtree on every retraction.
    assert.equal(t.entries[0], before, 'the untouched branch keeps its reference');
});

test('moveToEnd: the queued bubble moves to the bottom', () => {
    const t = new Transcript();
    t.append({ ...userEntry(1), uuid: 'asked' });
    t.append({ ...userEntry(2, 'queued'), uuid: 'queued' });
    t.append(assistantEntry(3, 'answer'));

    assert.equal(t.moveToEnd('queued'), true);

    // Without the move buildGroups would open the exchange on 2 and the answer to 1 would land
    // under the wrong question.
    assert.deepEqual(
        t.entries.map((e) => e.id),
        [1, 3, 2],
    );
});

test('moveToEnd: index stays consistent after the move', () => {
    const t = new Transcript();
    t.append({ ...userEntry(1), uuid: 'a' });
    t.append(toolEntry(2, 'agent'));
    t.appendChild('agent', userEntry(3), childKey);

    t.moveToEnd('a');

    assert.equal(t.find(1)?.id, 1, 'the moved entry stays reachable');
    assert.equal(t.find(3)?.id, 3, 'children shifted in position stay indexed');
});

test('moveToEnd: a missing uuid or one already last touches nothing', () => {
    const t = new Transcript();
    t.append({ ...userEntry(1), uuid: 'a' });
    t.append({ ...userEntry(2), uuid: 'b' });
    const before = t.entries;

    assert.equal(t.moveToEnd('never-seen'), false);
    assert.equal(t.moveToEnd('b'), false, 'already last');
    assert.equal(t.entries, before, 'no new array, Lit does not re-render');
});

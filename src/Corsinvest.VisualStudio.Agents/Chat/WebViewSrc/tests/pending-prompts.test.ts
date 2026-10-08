// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PendingPrompts } from '../core/pending-prompts.ts';

const prompt = (uuid: string, text: string, files = 0, images = 0) => ({
    uuid,
    text,
    // Only the count and the kind matter to this module.
    attachments: [
        ...Array.from({ length: images }, (_, i) => ({
            name: `i${i}.png`,
            mediaType: 'image/png',
        })),
        ...Array.from({ length: files }, (_, i) => ({
            name: `f${i}.txt`,
            mediaType: 'text/plain',
        })),
    ] as never[],
    later: false,
});
const shape = (text: string, images = 0, files = 0) => ({ text, images, files });

test('a prompt nobody sent mid-turn is none of our business', () => {
    const p = new PendingPrompts();
    assert.deepEqual(p.onReplay('typed-while-idle', shape('hello')), { kind: 'ignore' });
});

test('a pending prompt is read when its own replay comes back', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'fix the name'));
    assert.deepEqual(p.uuids, ['a']);

    assert.deepEqual(p.onReplay('a', shape('fix the name')), { kind: 'read', uuid: 'a' });
    assert.deepEqual(p.uuids, [], 'read is no longer pending');
});

test('two prompts read at a tool boundary stay two', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'end with APPLE'));
    p.add(prompt('b', 'include BANANA'));

    assert.deepEqual(p.onReplay('a', shape('end with APPLE')), { kind: 'read', uuid: 'a' });
    assert.deepEqual(p.onReplay('b', shape('include BANANA')), { kind: 'read', uuid: 'b' });
});

test('the replay that carries everything merges the ones read just before it', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'which colour', 0, 1));
    p.add(prompt('b', 'the secret word', 1, 0));
    p.add(prompt('c', 'end with CHERRY'));

    // End of turn, as measured: A and B come back alone, then C comes back holding all three.
    assert.equal(p.onReplay('a', shape('which colour', 1, 0)).kind, 'read');
    assert.equal(p.onReplay('b', shape('the secret word', 0, 1)).kind, 'read');
    assert.deepEqual(
        p.onReplay('c', shape('which colour\nthe secret word\nend with CHERRY', 1, 1)),
        { kind: 'merged', uuid: 'c', absorbed: ['a', 'b'] },
    );
    assert.deepEqual(p.uuids, []);
});

test('two prompts, the second containing the first, are not a merge', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'ok'));
    p.add(prompt('b', 'ok do it'));

    assert.equal(p.onReplay('a', shape('ok')).kind, 'read');
    // "ok do it" contains "ok", but a merge would hold both texts: it is too short to be one.
    assert.deepEqual(p.onReplay('b', shape('ok do it')), { kind: 'read', uuid: 'b' });
});

test('a prompt with only an attachment is still absorbed by the merge', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', '', 1, 0));
    p.add(prompt('b', 'read the file above'));

    assert.equal(p.onReplay('a', shape('', 0, 1)).kind, 'read');
    assert.deepEqual(p.onReplay('b', shape('read the file above', 0, 1)), {
        kind: 'merged',
        uuid: 'b',
        absorbed: ['a'],
    });
});

test('a slash command is read on its own and absorbs nothing', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'end with APPLE'));
    p.add(prompt('s', '/cost'));

    assert.equal(p.onReplay('a', shape('end with APPLE')).kind, 'read');
    // Its replay is the command's envelope, nothing like what was typed.
    assert.deepEqual(
        p.onReplay('s', shape('<command-name>/cost</command-name> end with APPLE and more text')),
        { kind: 'read', uuid: 's' },
    );
});

test('output from the model closes the batch: a later replay merges nothing from before', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'first'));
    p.add(prompt('b', 'second'));

    assert.equal(p.onReplay('a', shape('first')).kind, 'read');
    p.onTurnProgress();
    assert.deepEqual(p.onReplay('b', shape('first\nsecond')), { kind: 'read', uuid: 'b' });
});

test('whitespace differences between what was sent and what comes back do not make a merge', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'line one\nline two'));
    assert.deepEqual(p.onReplay('a', shape('  line one\r\nline two \n')), {
        kind: 'read',
        uuid: 'a',
    });
});

test('removed prompts are handed back, in send order, and are no longer pending', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'one'));
    p.add(prompt('b', 'two'));
    p.add(prompt('c', 'three'));

    const gone = p.remove(['c', 'a', 'unknown']);
    assert.deepEqual(
        gone.map((g) => g.uuid),
        ['a', 'c'],
    );
    assert.deepEqual(p.uuids, ['b']);
    assert.equal(p.has('a'), false);
});

test('a replay for a prompt already removed is ignored', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'one'));
    p.remove(['a']);
    assert.deepEqual(p.onReplay('a', shape('one')), { kind: 'ignore' });
});

test('clear forgets everything', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'one'));
    assert.equal(p.onReplay('a', shape('one')).kind, 'read');
    p.add(prompt('b', 'two'));
    p.clear();
    assert.deepEqual(p.uuids, []);
    assert.deepEqual(p.onReplay('b', shape('one\ntwo')), { kind: 'ignore' });
});

test('a prompt being taken back survives the notice that it is gone', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'fix the name', 1));
    p.beginTakeBack('a');
    // The CLI says "cancelled" on the lifecycle line a moment before it answers the request.
    p.remove(['a']);

    const back = p.endTakeBack('a', true);
    assert.equal(back?.text, 'fix the name');
    assert.equal(back?.attachments.length, 1);
    assert.equal(p.has('a'), false);
});

test('a take-back that came too late gives nothing back and leaves the prompt alone', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'one'));
    p.beginTakeBack('a');
    assert.equal(p.endTakeBack('a', false), undefined);
    assert.equal(p.has('a'), true, 'still pending: its replay will say where it went');
    assert.equal(
        p.endTakeBack('a', true),
        undefined,
        'the take-back was closed by the first answer',
    );
});

test('a second take-back of the same prompt starts nothing until the first is answered', () => {
    const p = new PendingPrompts();
    p.add(prompt('a', 'one'));
    assert.equal(p.beginTakeBack('a'), true);
    assert.equal(p.beginTakeBack('a'), false);
    p.endTakeBack('a', false);
    assert.equal(p.beginTakeBack('a'), true, 'an unanswered or refused one can be asked again');
});

test('taking back a prompt that is not pending starts nothing', () => {
    const p = new PendingPrompts();
    assert.equal(p.beginTakeBack('nope'), false);
    assert.equal(p.endTakeBack('nope', true), undefined);
});

test('busy while a turn runs or while something is still pending, and not otherwise', () => {
    const p = new PendingPrompts();
    assert.equal(p.busy, false);
    p.turnStarted();
    assert.equal(p.busy, true);
    p.add(prompt('a', 'one'));
    p.turnEnded();
    assert.equal(p.busy, true, 'the CLI starts a turn for what is pending');
    p.remove(['a']);
    assert.equal(p.busy, false, 'the last pending prompt went with no turn running');
});

test('drain hands everything back and leaves nothing pending and no turn running', () => {
    const p = new PendingPrompts();
    p.turnStarted();
    p.add(prompt('a', 'one'));
    p.add(prompt('b', 'two'));

    assert.deepEqual(
        p.drain().map((d) => d.uuid),
        ['a', 'b'],
    );
    assert.deepEqual(p.uuids, []);
    assert.equal(p.busy, false);
});

// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    ChangedFilesStore,
    countsNote,
    splitShownPath,
    stateLetter,
    undoBlocker,
} from '../core/changed-files-store.ts';
import type { ChangedFileDto } from '../core/generated/ChangedFileDto.ts';
import type { ChangedFilesNotification } from '../core/generated/ChangedFilesNotification.ts';

function file(path: string, extra: Partial<ChangedFileDto> = {}): ChangedFileDto {
    return {
        path,
        state: 'modified',
        source: 'edit',
        added: 0,
        deleted: 0,
        counts: 'exact',
        isBinary: false,
        hasBaseline: true,
        baselineMissing: false,
        incomplete: false,
        ...extra,
    };
}

function list(
    files: ChangedFileDto[],
    extra: Partial<ChangedFilesNotification> = {},
): ChangedFilesNotification {
    return { sessionId: 's1', files, overflow: false, maxFiles: 200, disabled: null, ...extra };
}

test('a new store has nothing to show', () => {
    const store = new ChangedFilesStore();

    assert.equal(store.files.length, 0);
    assert.equal(store.overflow, false);
    assert.equal(store.disabled, null);
});

test('apply replaces the list and keeps the order the host sent', () => {
    const store = new ChangedFilesStore();
    store.apply(list([file('C:\\b.txt'), file('C:\\a.txt')]));
    store.apply(list([file('C:\\z.txt'), file('C:\\c.txt')]));

    assert.deepEqual(
        store.files.map((f) => f.path),
        ['C:\\z.txt', 'C:\\c.txt'],
    );
});

test('apply carries the overflow and the reason nothing is tracked', () => {
    const store = new ChangedFilesStore();

    store.apply(list([], { overflow: true, maxFiles: 200 }));
    assert.equal(store.overflow, true);
    assert.equal(store.maxFiles, 200);

    store.apply(list([], { disabled: 'Keep file checkpoints is off.' }));
    assert.equal(store.disabled, 'Keep file checkpoints is off.');
    assert.equal(store.overflow, false);
});

test('every change notifies once, and an unsubscribed listener hears nothing more', () => {
    const store = new ChangedFilesStore();
    let heard = 0;
    const stop = store.subscribe(() => heard++);

    store.apply(list([file('C:\\a.txt')]));
    store.clear();
    assert.equal(heard, 2);

    stop();
    store.apply(list([file('C:\\a.txt')]));
    assert.equal(heard, 2);
});

test('clear empties the list and forgets a reason', () => {
    const store = new ChangedFilesStore();
    store.apply(list([file('C:\\a.txt')], { disabled: 'off' }));

    store.clear();

    assert.equal(store.files.length, 0);
    assert.equal(store.disabled, null);
});

test('a listener that unsubscribes while being notified does not break the others', () => {
    const store = new ChangedFilesStore();
    const heard: string[] = [];
    const stopFirst = store.subscribe(() => {
        heard.push('first');
        stopFirst();
    });
    store.subscribe(() => heard.push('second'));

    store.apply(list([]));

    assert.deepEqual(heard, ['first', 'second']);
});

test('splitShownPath separates the name from the folder, whatever the separator', () => {
    assert.deepEqual(splitShownPath('src/app/Program.cs'), {
        name: 'Program.cs',
        dir: 'src/app/',
    });
    assert.deepEqual(splitShownPath('C:\\proj\\a.txt'), { name: 'a.txt', dir: 'C:\\proj\\' });
    assert.deepEqual(splitShownPath('a.txt'), { name: 'a.txt', dir: '' });
});

test('stateLetter names the three states and treats anything else as modified', () => {
    assert.equal(stateLetter('added'), 'A');
    assert.equal(stateLetter('deleted'), 'D');
    assert.equal(stateLetter('modified'), 'M');
    assert.equal(stateLetter('whatever'), 'M');
});

test('countsNote says why there are no numbers, and nothing when there are', () => {
    assert.equal(countsNote({ counts: 'exact', isBinary: false }), null);
    assert.equal(countsNote({ counts: 'approximate', isBinary: false }), null);
    assert.equal(countsNote({ counts: 'unavailable', isBinary: false }), 'not counted');
    assert.equal(countsNote({ counts: 'unavailable', isBinary: true }), 'binary');
});

test('undoBlocker is null when there is a baseline, else says which kind of absence', () => {
    assert.equal(undoBlocker({ hasBaseline: true, baselineMissing: false }), null);
    assert.match(undoBlocker({ hasBaseline: false, baselineMissing: true }) ?? '', /is gone/);
    assert.match(undoBlocker({ hasBaseline: false, baselineMissing: false }) ?? '', /no copy/);
});

// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { StateSubscriptions } from '../core/state-subscriptions.ts';
import { state as appState } from '../core/state.ts';

/**
 * Stand-in for a LitElement: enough of ReactiveControllerHost to drive the lifecycle by hand,
 * following what ReactiveElement itself does — connectedCallback calls hostConnected on every
 * controller, disconnectedCallback calls hostDisconnected.
 */
class FakeHost implements ReactiveControllerHost {
    readonly controllers: ReactiveController[] = [];
    updateCount = 0;

    addController(c: ReactiveController): void {
        this.controllers.push(c);
    }
    removeController(c: ReactiveController): void {
        const i = this.controllers.indexOf(c);
        if (i >= 0) {
            this.controllers.splice(i, 1);
        }
    }
    requestUpdate(): void {
        this.updateCount++;
    }
    get updateComplete(): Promise<boolean> {
        return Promise.resolve(true);
    }

    connect(): void {
        this.controllers.forEach((c) => c.hostConnected?.());
    }
    disconnect(): void {
        this.controllers.forEach((c) => c.hostDisconnected?.());
    }
}

/** How many callbacks the store keeps alive for a key: the only measure that sees a
 *  subscription left dangling, which counting the calls would not reveal. */
function liveListeners(key: string): number {
    const subs = (appState as unknown as { _subs?: Map<string, Set<unknown>> })._subs;
    return subs?.get(key)?.size ?? 0;
}

test('the controller registers on the host at construction', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);

    assert.deepEqual(host.controllers, [subs]);
});

test('no subscription is live before connect', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));

    appState.workingDirectory = 'k:/before-connect';

    assert.deepEqual(seen, []);
});

test('after connect the callback receives the changes', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));
    host.connect();

    appState.workingDirectory = 'k:/one';
    appState.workingDirectory = 'k:/two';

    assert.deepEqual(seen, ['k:/one', 'k:/two']);
});

test('disconnect detaches everything', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));
    host.connect();
    appState.workingDirectory = 'k:/live';
    host.disconnect();

    appState.workingDirectory = 'k:/after-disconnect';

    assert.deepEqual(seen, ['k:/live']);
});

test('a host removed and re-inserted receives changes again', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));

    host.connect();
    appState.workingDirectory = 'k:/first';
    host.disconnect();
    appState.workingDirectory = 'k:/while-detached';
    host.connect();
    appState.workingDirectory = 'k:/second';

    assert.deepEqual(seen, ['k:/first', 'k:/second']);
});

// The leaking case: two connects in a row with no disconnect in between. Lit calls
// hostConnected on every connectedCallback, and addController calls it right away if the host is
// already connected. If the second round overwrote the unsubscribe list, the first would stay
// dangling - invisible when counting calls, because the store keeps listeners in a Set that
// deduplicates the same function. The store is what must be measured, not the callback: after the
// disconnect it must be back to exactly the starting count.
test('a repeated connect leaves no dangling subscriptions in the store', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    // A fresh closure each time: two distinct subscriptions, which a Set cannot merge.
    subs.on('workingDirectory', (v) => void v);
    subs.on('workingDirectory', (v) => void v);
    const before = liveListeners('workingDirectory');

    host.connect();
    host.connect();
    host.disconnect();

    assert.equal(liveListeners('workingDirectory'), before);
});

test('a repeated connect does not fire the callback twice', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));

    host.connect();
    host.connect();
    appState.workingDirectory = 'k:/once';

    assert.deepEqual(seen, ['k:/once']);
});

test('rerenderOn asks for an update per key, without reading the value', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    subs.rerenderOn('currentModel', 'permissionMode');
    host.connect();

    appState.currentModel = 'opus';
    appState.permissionMode = 'plan';

    assert.equal(host.updateCount, 2);
});

test('rerenderOn stops asking for updates after disconnect', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    subs.rerenderOn('currentModel');
    host.connect();
    appState.currentModel = 'sonnet';
    host.disconnect();

    appState.currentModel = 'haiku';

    assert.equal(host.updateCount, 1);
});

test('two independent hosts do not interfere', () => {
    const a = new FakeHost();
    const b = new FakeHost();
    const subsA = new StateSubscriptions(a);
    const subsB = new StateSubscriptions(b);
    subsA.rerenderOn('currentModel');
    subsB.rerenderOn('currentModel');
    a.connect();
    b.connect();

    appState.currentModel = 'first';
    a.disconnect();
    appState.currentModel = 'second';

    assert.equal(a.updateCount, 1);
    assert.equal(b.updateCount, 2);
});

// Every on() call in the components sits in the constructor, so before the first connect. One
// arriving later would stay mute until re-insertion in the DOM: the controller subscribes only in
// hostConnected. Pinned here because it is the boundary of the contract, not an implementation
// detail.
test('on() after connect has no effect until re-insertion', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    host.connect();

    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));
    appState.workingDirectory = 'k:/late-subscription';

    assert.deepEqual(seen, [], 'a late on() does not start on its own');

    host.disconnect();
    host.connect();
    appState.workingDirectory = 'k:/after-reconnect';

    assert.deepEqual(seen, ['k:/after-reconnect']);
});

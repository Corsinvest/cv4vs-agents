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

/** Quante callback lo store tiene vive per una chiave: l'unica misura che vede una
 *  sottoscrizione rimasta appesa, che contare le chiamate non rivelerebbe. */
function liveListeners(key: string): number {
    const subs = (appState as unknown as { _subs?: Map<string, Set<unknown>> })._subs;
    return subs?.get(key)?.size ?? 0;
}

test('il controller si registra sull host alla costruzione', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);

    assert.deepEqual(host.controllers, [subs]);
});

test('nessuna sottoscrizione è viva prima del connect', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));

    appState.workingDirectory = 'k:/before-connect';

    assert.deepEqual(seen, []);
});

test('dopo il connect la callback riceve i cambi', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));
    host.connect();

    appState.workingDirectory = 'k:/one';
    appState.workingDirectory = 'k:/two';

    assert.deepEqual(seen, ['k:/one', 'k:/two']);
});

test('il disconnect stacca tutto', () => {
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

test('un host rimosso e reinserito torna a ricevere i cambi', () => {
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

// Il caso che perderebbe memoria: due connect di fila senza disconnect in mezzo. Lit chiama
// hostConnected a ogni connectedCallback, e addController lo chiama subito se l'host è già
// connesso. Se il secondo giro sovrascrivesse la lista di unsubscribe, il primo resterebbe
// appeso — invisibile contando le chiamate, perché lo store tiene i listener in un Set che
// deduplica la stessa funzione. Va misurato lo store, non la callback: dopo il disconnect
// deve tornare esattamente al numero di partenza.
test('un connect ripetuto non lascia sottoscrizioni appese nello store', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    // Una chiusura nuova a ogni giro: due sottoscrizioni distinte, che un Set non può fondere.
    subs.on('workingDirectory', (v) => void v);
    subs.on('workingDirectory', (v) => void v);
    const before = liveListeners('workingDirectory');

    host.connect();
    host.connect();
    host.disconnect();

    assert.equal(liveListeners('workingDirectory'), before);
});

test('un connect ripetuto non fa scattare la callback due volte', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));

    host.connect();
    host.connect();
    appState.workingDirectory = 'k:/once';

    assert.deepEqual(seen, ['k:/once']);
});

test('rerenderOn chiede un update per ogni chiave, senza leggere il valore', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    subs.rerenderOn('currentModel', 'permissionMode');
    host.connect();

    appState.currentModel = 'opus';
    appState.permissionMode = 'plan';

    assert.equal(host.updateCount, 2);
});

test('rerenderOn smette di chiedere update dopo il disconnect', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    subs.rerenderOn('currentModel');
    host.connect();
    appState.currentModel = 'sonnet';
    host.disconnect();

    appState.currentModel = 'haiku';

    assert.equal(host.updateCount, 1);
});

test('due host indipendenti non si disturbano', () => {
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

// Tutte le chiamate a on() dei componenti stanno nel constructor, quindi prima del primo
// connect. Se una arrivasse dopo, resterebbe muta fino al reinserimento nel DOM: il
// controller sottoscrive solo in hostConnected. Fissato qui perché è il limite del
// contratto, non un dettaglio implementativo.
test('on() dopo il connect non ha effetto fino al reinserimento', () => {
    const host = new FakeHost();
    const subs = new StateSubscriptions(host);
    host.connect();

    const seen: string[] = [];
    subs.on('workingDirectory', (v) => seen.push(v));
    appState.workingDirectory = 'k:/late-subscription';

    assert.deepEqual(seen, [], 'una on() tardiva non parte da sola');

    host.disconnect();
    host.connect();
    appState.workingDirectory = 'k:/after-reconnect';

    assert.deepEqual(seen, ['k:/after-reconnect']);
});

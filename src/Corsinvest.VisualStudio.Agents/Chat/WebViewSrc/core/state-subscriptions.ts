/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { state as appState, type AppState } from './state';

/**
 * Holds a component's appState subscriptions and drops them when it leaves the DOM.
 *
 * Lit reconnects a host that is moved rather than destroyed, so the subscriptions are
 * re-established on hostConnected: an element that survives a re-parent keeps working.
 */
export class StateSubscriptions implements ReactiveController {
    private readonly _pending: Array<() => () => void> = [];
    private _offs: Array<() => void> = [];

    private readonly _host: ReactiveControllerHost;

    constructor(host: ReactiveControllerHost) {
        this._host = host;
        host.addController(this);
    }

    /** Subscribe for as long as the host is connected. */
    on<K extends keyof AppState>(key: K, fn: (value: AppState[K]) => void): void {
        this._pending.push(() => appState.on(key, fn));
    }

    /** Subscribe to re-render only, for a render that reads appState itself. */
    rerenderOn(...keys: Array<keyof AppState>): void {
        for (const key of keys) {
            this.on(key, () => this._host.requestUpdate());
        }
    }

    hostConnected(): void {
        this._offs = this._pending.map((subscribe) => subscribe());
    }

    hostDisconnected(): void {
        this._offs.forEach((off) => off());
        this._offs = [];
    }
}

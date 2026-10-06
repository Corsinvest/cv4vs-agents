// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import type { CommandHost } from '../core/commands/base.ts';
import {
    EffortCommand,
    UltracodeCommand,
    ultracodeActive,
} from '../core/commands/model-controls.ts';
import { state as appState } from '../core/state.ts';
import type { ModelInfoDto } from '../core/types.ts';

function model(value: string, levels: string[]): ModelInfoDto {
    return {
        value,
        resolvedModel: value,
        displayName: value,
        description: '',
        supportsEffort: levels.length > 0,
        supportedEffortLevels: levels,
        supportsFastMode: false,
        supportsAdaptiveThinking: false,
        supportsAutoMode: false,
        disabled: false,
    };
}

function recordingHost(): { host: CommandHost; sent: Array<Record<string, unknown>> } {
    const sent: Array<Record<string, unknown>> = [];
    const host = { applyFlagSettings: (s: Record<string, unknown>) => sent.push(s) };
    return { host: host as unknown as CommandHost, sent };
}

beforeEach(() => {
    appState.models = [
        model('opus', ['low', 'medium', 'high', 'xhigh', 'max']),
        model('sonnet-4-6', ['low', 'medium', 'high', 'max']),
        model('haiku', []),
    ];
    appState.currentModel = 'opus';
    appState.effortLevel = 'medium';
    appState.ultracodeEnabled = false;
});

test('the effort slider offers the model levels and nothing else', () => {
    const ctrl = new EffortCommand().trailingControl;
    assert.equal(ctrl.kind, 'slider');
    assert.deepEqual(ctrl.kind === 'slider' ? ctrl.stops.map((s) => s.label) : [], [
        'Low',
        'Medium',
        'High',
        'Extra high',
        'Max',
    ]);
});

test('changing the effort sends only the level, so ultracode is left as it is', () => {
    const { host, sent } = recordingHost();
    appState.ultracodeEnabled = true;

    new EffortCommand().setLevel(host, 2);

    assert.deepEqual(sent, [{ effortLevel: 'high' }]);
    assert.equal(appState.effortLevel, 'high');
    assert.equal(appState.ultracodeEnabled, true);
});

test('turning ultracode on leaves the effort alone', () => {
    const { host, sent } = recordingHost();

    new UltracodeCommand().run(host);

    assert.deepEqual(sent, [{ ultracode: true }]);
    assert.equal(appState.effortLevel, 'medium');
    assert.equal(ultracodeActive(), true);
});

test('turning ultracode off removes the key instead of storing false', () => {
    const { host, sent } = recordingHost();
    appState.ultracodeEnabled = true;

    new UltracodeCommand().run(host);

    assert.deepEqual(sent, [{ ultracode: null }]);
    assert.equal(ultracodeActive(), false);
});

test('ultracode is offered only on a model that lists xhigh', () => {
    const cmd = new UltracodeCommand();
    assert.equal(cmd.isEnabled(), true);

    appState.currentModel = 'sonnet-4-6';
    assert.equal(cmd.isEnabled(), false);

    appState.currentModel = 'haiku';
    assert.equal(cmd.isEnabled(), false);
});

test('ultracode asked for on one model is not in effect on a model that cannot run it', () => {
    appState.ultracodeEnabled = true;
    assert.equal(ultracodeActive(), true);

    appState.currentModel = 'sonnet-4-6';
    assert.equal(ultracodeActive(), false);
    assert.equal(new UltracodeCommand().checked, false);

    appState.currentModel = 'opus';
    assert.equal(ultracodeActive(), true);
});

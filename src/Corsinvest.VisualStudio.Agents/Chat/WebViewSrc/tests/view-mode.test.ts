// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test, beforeEach } from 'node:test';
import assert from 'node:assert/strict';
import type { CommandHost } from '../core/commands/base.ts';
import { ViewModeCommand, SET_VIEW_MODE_COMMANDS } from '../core/commands/view-mode.ts';
import { state as appState } from '../core/state.ts';
import type { ViewMode } from '../core/types.ts';

function recordingHost(): { host: CommandHost; sent: ViewMode[] } {
    const sent: ViewMode[] = [];
    const host = { setViewMode: (m: ViewMode) => sent.push(m) };
    return { host: host as unknown as CommandHost, sent };
}

beforeEach(() => {
    appState.ui = { ...appState.ui, viewMode: 'full' };
});

test('one direct command per mode, named and numbered by how much it hides', () => {
    assert.deepEqual(
        SET_VIEW_MODE_COMMANDS.map((c) => [c.label, c.aliases[0]]),
        [
            ['/vm:full', 'vm:0'],
            ['/vm:focus', 'vm:1'],
            ['/vm:hide', 'vm:2'],
        ],
    );
});

test('a direct command sets its mode whatever the current one is', () => {
    const { host, sent } = recordingHost();
    const hide = SET_VIEW_MODE_COMMANDS[2];

    hide.run(host);
    assert.equal(appState.ui.viewMode, 'hideToolCalls');
    hide.run(host);
    assert.equal(appState.ui.viewMode, 'hideToolCalls');

    SET_VIEW_MODE_COMMANDS[1].run(host);
    assert.equal(appState.ui.viewMode, 'focus');
    assert.deepEqual(sent, ['hideToolCalls', 'hideToolCalls', 'focus']);
});

test('only the command of the current mode is marked', () => {
    appState.ui = { ...appState.ui, viewMode: 'focus' };
    assert.deepEqual(
        SET_VIEW_MODE_COMMANDS.map((c) => c.trailingControl?.kind === 'value'),
        [false, true, false],
    );
});

test('the direct commands close the menu, the slider row keeps it open', () => {
    assert.ok(SET_VIEW_MODE_COMMANDS.every((c) => !c.keepMenuOpen));
    assert.equal(new ViewModeCommand().keepMenuOpen, true);
});

// `focus` and `compact` are commands of the CLI: an alias equal to one ties with it for the same
// typed name, and the native row won.
test('no View mode command takes a name the CLI uses', () => {
    const taken = [new ViewModeCommand(), ...SET_VIEW_MODE_COMMANDS].flatMap((c) => c.aliases);
    assert.ok(!taken.includes('focus'));
    assert.ok(!taken.includes('compact'));
});

test('the slider row sorts before the direct commands, which keep the order of the stops', () => {
    assert.deepEqual(
        [new ViewModeCommand(), ...SET_VIEW_MODE_COMMANDS].map((c) => c.order),
        [0, 1, 2, 3],
    );
});

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import TextBulletListSquare16Regular from '@fluentui/svg-icons/icons/text_bullet_list_square_16_regular.svg';
import {
    ChatCommand,
    type CommandHost,
    type CommandSection,
    type CommandTrailing,
    type TrailingControl,
} from './base';
import { state as appState } from '../state';
import type { ViewMode } from '../types';

/** `name` is the direct command (`/vm:<name>`); its numeric alias is the stop's index, which is
 *  also how much the mode hides: 0 nothing, 2 the most. */
const STOPS: ReadonlyArray<{ mode: ViewMode; label: string; name: string; shows: string }> = [
    { mode: 'full', label: 'Full', name: 'full', shows: 'every row' },
    {
        mode: 'focus',
        label: 'Focus',
        name: 'focus',
        shows: 'each run of tool calls folds into one row',
    },
    {
        mode: 'hideToolCalls',
        label: 'Hide tools',
        name: 'hide',
        shows: 'the tool rows are removed',
    },
];

/** Applied here at once, not on the host's echo: the chat changes under the open menu. */
function applyViewMode(host: CommandHost, mode: ViewMode): void {
    appState.ui = { ...appState.ui, viewMode: mode };
    host.setViewMode(mode);
}

/** One setting for every open chat: the host saves it and pushes it back to all of them. */
export class ViewModeCommand extends ChatCommand {
    readonly id = 'view-mode';
    readonly label = 'View mode';
    readonly description = "How much of Claude's work the chat shows";
    readonly section: CommandSection = 'settings';
    readonly icon = TextBulletListSquare16Regular;
    readonly trailing: CommandTrailing = 'slider';
    readonly keepMenuOpen = true;
    // Not `focus` nor `compact`: both are commands of the CLI, and an alias equal to one put this
    // row in a tie with it for the same typed name.
    override readonly aliases = ['view', 'hide', 'tools', 'vm'];

    get level(): number {
        return Math.max(
            0,
            STOPS.findIndex((s) => s.mode === appState.ui.viewMode),
        );
    }

    setLevel(host: CommandHost, idx: number): void {
        applyViewMode(host, STOPS[Math.max(0, Math.min(idx, STOPS.length - 1))].mode);
    }

    override get trailingControl(): TrailingControl {
        return {
            kind: 'slider',
            stops: STOPS.map((s, i) => ({ label: s.label, value: i })),
            value: this.level,
            label: STOPS[this.level].label,
            onSet: (host, v) => this.setLevel(host, v),
        };
    }

    /** Click on the row cycles to the next stop (the slider handles drags). */
    override run(host: CommandHost): void {
        this.setLevel(host, (this.level + 1) % STOPS.length);
    }
}

/**
 * One mode, set outright: `/vm:focus` (or `/vm:1`) and Enter, with no slider to reach for. The
 * slider row above cycles on Enter, so it cannot be asked for a given mode from the keyboard.
 */
export class SetViewModeCommand extends ChatCommand {
    readonly section: CommandSection = 'settings';
    readonly icon = TextBulletListSquare16Regular;
    readonly trailing: CommandTrailing = 'value';
    readonly id: string;
    readonly label: string;
    readonly description: string;
    override readonly order: number;
    override readonly aliases: readonly string[];
    private readonly mode: ViewMode;

    constructor(index: number) {
        super();
        const stop = STOPS[index];
        this.mode = stop.mode;
        this.id = `view-mode:${stop.name}`;
        this.label = `/vm:${stop.name}`;
        this.description = `View mode ${stop.label}: ${stop.shows}`;
        this.aliases = [`vm:${index}`];
        // After the slider row (order 0), in the order of the stops.
        this.order = index + 1;
    }

    /** Marks the mode the chat is in, so the three rows read as a choice already made. */
    override get trailingControl(): TrailingControl | null {
        return appState.ui.viewMode === this.mode ? { kind: 'value', label: 'Current' } : null;
    }

    override run(host: CommandHost): void {
        applyViewMode(host, this.mode);
    }
}

export const SET_VIEW_MODE_COMMANDS: readonly SetViewModeCommand[] = STOPS.map(
    (_, i) => new SetViewModeCommand(i),
);

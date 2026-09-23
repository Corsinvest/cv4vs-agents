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

const STOPS: ReadonlyArray<{ mode: ViewMode; label: string }> = [
    { mode: 'full', label: 'Full' },
    { mode: 'focus', label: 'Focus' },
    { mode: 'hideToolCalls', label: 'Hide tools' },
];

/** One setting for every open chat: the host saves it and pushes it back to all of them. */
export class ViewModeCommand extends ChatCommand {
    readonly id = 'view-mode';
    readonly label = 'View mode';
    readonly description = "How much of Claude's work the chat shows";
    readonly section: CommandSection = 'settings';
    readonly icon = TextBulletListSquare16Regular;
    readonly trailing: CommandTrailing = 'slider';
    readonly keepMenuOpen = true;
    override readonly aliases = ['view', 'focus', 'hide', 'tools', 'compact'];

    get level(): number {
        return Math.max(
            0,
            STOPS.findIndex((s) => s.mode === appState.ui.viewMode),
        );
    }

    /** Applied here at once, not on the host's echo: the chat changes under the open menu. */
    setLevel(host: CommandHost, idx: number): void {
        const mode = STOPS[Math.max(0, Math.min(idx, STOPS.length - 1))].mode;
        appState.ui = { ...appState.ui, viewMode: mode };
        host.setViewMode(mode);
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

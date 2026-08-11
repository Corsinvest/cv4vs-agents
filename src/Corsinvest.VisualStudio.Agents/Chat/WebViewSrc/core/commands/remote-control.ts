/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { ChatCommand, type CommandHost, type CommandSection, type CommandTrailing } from './base';
import { state as appState } from '../state';
import PhoneDesktop16Regular from '@fluentui/svg-icons/icons/phone_desktop_16_regular.svg';

export class RemoteControlCommand extends ChatCommand {
    readonly id = 'remote-control';
    readonly label = 'Remote Control';
    readonly description = 'Continue this session from your phone or claude.ai/code';
    readonly section: CommandSection = 'context';
    readonly order = 45;
    readonly icon = PhoneDesktop16Regular;
    readonly trailing: CommandTrailing = 'toggle';
    override readonly aliases = ['remote', 'rc', 'phone'];
    readonly keepMenuOpen = true;

    get checked(): boolean {
        return appState.remoteControl.status === 'connected';
    }

    /** Off while the bridge is coming up: the answer is a round-trip away and a second click
     *  would fight the first. */
    override isEnabled(): boolean {
        return appState.remoteControl.status !== 'connecting';
    }

    override run(host: CommandHost): void {
        host.setRemoteControl(!this.checked);
    }
}

/** The CLI's `detail` is written for a terminal: "/login" alone means nothing to someone who has
 *  never used it. Wrap the ones we know, fall through to the raw text for the rest — a technical
 *  string beats nothing. */
export function remoteControlErrorText(detail?: string): string {
    switch (detail) {
        case '/login':
            return 'Remote Control needs a claude.ai login — run /login in a terminal.';
        case "disabled by your organization's policy":
            return "Remote Control is disabled by your organization's policy.";
        case 'run `claude update` to upgrade':
            return 'Your Claude CLI is too old for Remote Control — run claude update.';
        case 'connection to server lost':
        case 'reconnection failed':
            return 'Remote Control lost its connection.';
        default:
            return detail || 'Remote Control failed to start.';
    }
}

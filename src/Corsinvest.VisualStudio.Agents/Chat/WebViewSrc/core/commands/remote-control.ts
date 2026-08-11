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

/** The CLI writes its own reasons — "/login", "disabled by your organization's policy",
 *  "connection to server lost" — and they read fine behind a prefix. */
export function remoteControlErrorText(detail?: string): string {
    return detail ? `Remote Control error: ${detail}` : 'Remote Control failed to start.';
}

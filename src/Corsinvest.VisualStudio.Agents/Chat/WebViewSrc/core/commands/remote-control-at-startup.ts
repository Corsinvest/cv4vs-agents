/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { ChatCommand, type CommandHost, type CommandSection, type CommandTrailing } from './base';
import { state as appState } from '../state';
import { REMOTE_CONTROL_ICON } from './builtin-commands';

/** remoteControlAtStartup in this profile's settings.json, for NEW sessions only. The chats
 *  already open keep their own Remote Control button, so the description says "new sessions":
 *  the VS Code wording ("Connect all sessions…") would promise the open ones too. */
export class RemoteControlAtStartupCommand extends ChatCommand {
    readonly id = 'remote-control-at-startup';
    readonly label = 'Enable Remote Control for all sessions';
    readonly description = 'Start Remote Control automatically in new sessions of this profile';
    readonly section: CommandSection = 'settings';
    readonly icon = REMOTE_CONTROL_ICON;
    readonly trailing: CommandTrailing = 'toggle';
    readonly keepMenuOpen = true;
    override readonly aliases = ['remote', 'rc', 'startup', 'phone'];
    get checked(): boolean {
        return appState.remoteControlAtStartup;
    }
    override isEnabled(): boolean {
        return appState.remoteControlAvailable;
    }
    override run(host: CommandHost): void {
        host.setRemoteControlAtStartup(!this.checked);
    }
}

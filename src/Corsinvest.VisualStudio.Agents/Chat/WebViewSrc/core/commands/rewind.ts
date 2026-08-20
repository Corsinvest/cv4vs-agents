/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import ArrowCounterclockwise16Regular from '@fluentui/svg-icons/icons/arrow_counterclockwise_16_regular.svg';
import { ChatCommand, type CommandHost, type CommandSection } from './base';
import { state } from '../state';

/**
 * Context · Rewind — restore the files to the state the CLI captured before a chosen message.
 *
 * Files only: the conversation stays as it is. Undoing the conversation too is what /clear and the
 * fork action are for, and folding three different retreats into one command would make each of
 * them harder to reach for.
 *
 * Routed through the host rather than opening the dialog here: the list of messages comes from the
 * transcript, which a command has no way to read.
 */
export class RewindCommand extends ChatCommand {
    readonly id = 'rewind';
    readonly label = 'Rewind files…';
    readonly description = 'Restore the files to how they were before a message';
    readonly section: CommandSection = 'context';
    readonly order = 41;
    readonly icon = ArrowCounterclockwise16Regular;
    override readonly aliases = ['rewind', 'restore', 'checkpoint', 'undo'];
    /** Hidden when the pane was started without file checkpointing (Options → Chat): there are no
     *  snapshots to go back to, so the dialog could only ever say so. */
    override isEnabled(): boolean {
        return state.ui.fileCheckpoints;
    }
    override run(host: CommandHost): void {
        host.openRewind();
    }
}

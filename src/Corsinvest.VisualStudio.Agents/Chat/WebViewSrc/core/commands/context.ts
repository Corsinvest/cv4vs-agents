/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import DataUsage16Regular from '@fluentui/svg-icons/icons/data_usage_16_regular.svg';
import { ChatCommand, type CommandSection } from './base';
import { openContextDialog } from '../dialog-host';

/** Model · Context usage — opens the modal dialog with the current session's
 *  context-window breakdown (categories, memory-map, per-message detail, tree)
 *  from the CLI's get_context_usage. Distinct from /usage (plan/account). */
export class ContextCommand extends ChatCommand {
    readonly id = 'context';
    readonly label = 'Context usage…';
    readonly description = 'View the current session context window breakdown';
    readonly section: CommandSection = 'model';
    readonly order = 51;
    readonly icon = DataUsage16Regular;
    override readonly aliases = ['context', 'tokens', 'window'];
    override run(): void {
        openContextDialog();
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import DataPie16Regular from '@fluentui/svg-icons/icons/data_pie_16_regular.svg';
import { ChatCommand, type CommandSection } from './base';
import { openUsageDialog } from '../dialog-host';

/** Model · Account & usage — opens the modal dialog (session cost + plan
 *  rate-limit windows from the CLI's experimental get_usage). */
export class UsageCommand extends ChatCommand {
    readonly id = 'usage';
    readonly label = 'Account & usage…';
    readonly description = 'View account info and plan usage';
    readonly section: CommandSection = 'model';
    readonly order = 50;
    readonly icon = DataPie16Regular;
    override readonly aliases = ['usage', 'account', 'cost', 'billing', 'plan'];
    override run(): void {
        openUsageDialog();
    }
}

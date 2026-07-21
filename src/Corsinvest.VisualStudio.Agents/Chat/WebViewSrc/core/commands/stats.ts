/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import DataHistogram16Regular from '@fluentui/svg-icons/icons/data_histogram_16_regular.svg';
import { ChatCommand, type CommandSection } from './base';
import { openStatsDialog } from '../dialog-host';

/** Model · Statistics — opens the modal dialog with historical usage stats
 *  (tokens, sessions, streaks, heatmap, per-model breakdown) aggregated from the
 *  local session .jsonl. Scope All/Project/Session + range All/30d/7d. Ours, not
 *  the CLI's — distinct from /usage (plan) and /context (current window). */
export class StatsCommand extends ChatCommand {
    readonly id = 'stats';
    readonly label = 'Statistics…';
    readonly description = 'View historical usage statistics';
    readonly section: CommandSection = 'model';
    readonly order = 52;
    readonly icon = DataHistogram16Regular;
    override readonly aliases = ['stats', 'statistics', 'metrics', 'history'];
    override run(): void {
        openStatsDialog();
    }
}

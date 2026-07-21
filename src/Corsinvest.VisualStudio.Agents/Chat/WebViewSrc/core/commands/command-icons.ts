/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import Broom16Regular from '@fluentui/svg-icons/icons/broom_16_regular.svg';
import Brain16Regular from '@fluentui/svg-icons/icons/brain_16_regular.svg';
import Settings16Regular from '@fluentui/svg-icons/icons/settings_16_regular.svg';
import Bug16Regular from '@fluentui/svg-icons/icons/bug_16_regular.svg';
import Gauge16Regular from '@fluentui/svg-icons/icons/gauge_16_regular.svg';
import Lightbulb16Regular from '@fluentui/svg-icons/icons/lightbulb_16_regular.svg';
import Rocket16Regular from '@fluentui/svg-icons/icons/rocket_16_regular.svg';
import Dumbbell16Regular from '@fluentui/svg-icons/icons/dumbbell_16_regular.svg';
import Stethoscope20Regular from '@fluentui/svg-icons/icons/stethoscope_20_regular.svg';
import DocumentAdd16Regular from '@fluentui/svg-icons/icons/document_add_16_regular.svg';
import Rename16Regular from '@fluentui/svg-icons/icons/rename_16_regular.svg';
import ArrowMinimize16Regular from '@fluentui/svg-icons/icons/arrow_minimize_16_regular.svg';
import Play16Regular from '@fluentui/svg-icons/icons/play_16_regular.svg';
import Bot16Regular from '@fluentui/svg-icons/icons/bot_16_regular.svg';
import ArrowRepeatAll16Regular from '@fluentui/svg-icons/icons/arrow_repeat_all_16_regular.svg';

// Icons for slash commands the CLI reports via `initialize` (no icon of their own).
// Hand-curated: keyed by the bare command name (no leading "/"). Add entries as needed —
// an unmapped name simply renders without an icon. Reused by the context gauge for /compact.
// Names that overlap a built-in command reuse the SAME icon as builtin-commands.ts.
const COMMAND_ICONS: Readonly<Record<string, string>> = {
    compact: ArrowMinimize16Regular,
    clear: Broom16Regular,
    model: Brain16Regular,
    config: Settings16Regular,
    bug: Bug16Regular,
    debug: Bug16Regular,
    context: Gauge16Regular,
    usage: Gauge16Regular,
    thinking: Lightbulb16Regular,
    fast: Rocket16Regular,
    effort: Dumbbell16Regular,
    doctor: Stethoscope20Regular,
    init: DocumentAdd16Regular,
    rename: Rename16Regular,
    run: Play16Regular,
    agents: Bot16Regular,
    loop: ArrowRepeatAll16Regular,
};

/** SVG for a CLI slash command by its bare name (e.g. "compact"), or undefined if unmapped. */
export function iconForCommandName(name: string): string | undefined {
    return COMMAND_ICONS[name];
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Public surface of the chat-commands module. Consumers import from here, not
// from individual command files.
export { ChatCommand } from './base';
export type { CommandHost, CommandSection, CommandTrailing } from './base';
export { SlashCommand } from './slash';
export {
    SECTIONS,
    STATIC_COMMANDS,
    allCommands,
    dynamicSlashCommands,
    groupCommands,
    type CommandGroup,
} from './registry';

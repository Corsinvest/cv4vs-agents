/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { state } from '../state';
import { ChatCommand, type CommandSection } from './base';
import {
    AttachFileCommand,
    MentionFileCommand,
    ClearCommand,
    NewConversationCommand,
    ResumeConversationCommand,
    SwitchModelCommand,
    SwitchPermissionModeCommand,
    GeneralConfigCommand,
    ManagePluginsCommand,
    OpenCliTerminalCommand,
    HelpDocsCommand,
    ReportProblemCommand,
} from './builtin-commands';
import { UsageCommand } from './usage';
import { ContextCommand } from './context';
import { StatsCommand } from './stats';
import {
    ThinkingCommand,
    FastModeCommand,
    EffortCommand,
    SwitchModelsOnFlagCommand,
} from './model-controls';
import { SlashCommand } from './slash';

/**
 * Static commands we own the wiring for. Unordered here — the menu groups by
 * section and sorts by each command's `order` (then label). Add a command by
 * dropping a file in this folder and listing its class below; it appears in its
 * section automatically. Dynamic `/name` commands are NOT here (see below).
 */
export const STATIC_COMMANDS: readonly ChatCommand[] = [
    new AttachFileCommand(),
    new MentionFileCommand(),
    new ClearCommand(),
    new NewConversationCommand(),
    new ResumeConversationCommand(),
    new SwitchModelCommand(),
    new SwitchPermissionModeCommand(),
    new EffortCommand(),
    new ThinkingCommand(),
    new FastModeCommand(),
    new SwitchModelsOnFlagCommand(),
    new UsageCommand(),
    new ContextCommand(),
    new StatsCommand(),
    new GeneralConfigCommand(),
    new ManagePluginsCommand(),
    new OpenCliTerminalCommand(),
    new HelpDocsCommand(),
    new ReportProblemCommand(),
];

/** Section headings + the fixed display order (mirrors VS Code's menu). A
 *  section with no commands is skipped by the menu, so listing them all here
 *  costs nothing and lets future commands appear just by setting `section`. */
export const SECTIONS: ReadonlyArray<{ id: CommandSection; label: string }> = [
    { id: 'context', label: 'Context' },
    { id: 'model', label: 'Model' },
    { id: 'customize', label: 'Customize' },
    { id: 'slash', label: 'Slash Commands' },
    { id: 'settings', label: 'Settings' },
    { id: 'support', label: 'Support' },
];

/** Names already covered by a static command that would otherwise show twice.
 *  Empty now: `/clear` is kept (the CLI runs it → conversation_reset, which we
 *  handle) alongside our curated "Clear conversation"; `/usage` and `/context`
 *  are kept and remapped to our dialogs inside SlashCommand.run(). */
const STATIC_SLASH_NAMES = new Set<string>();

/** Build the dynamic `/name` commands from app state — the rich list from the CLI's
 *  `initialize` catalogue / `commands_changed` (name + description + hint + aliases). */
export function dynamicSlashCommands(): SlashCommand[] {
    return state.slashCommands
        .map((c) => new SlashCommand(c.name, c.description, c.argumentHint, c.aliases))
        .filter((c) => !STATIC_SLASH_NAMES.has(c.name));
}

/** All commands (static + dynamic), unsorted. The menu groups/sorts these. */
export function allCommands(): ChatCommand[] {
    return [...STATIC_COMMANDS, ...dynamicSlashCommands()];
}

/** A section heading plus its commands, sorted by `order` then label A–Z. */
export interface CommandGroup {
    section: CommandSection;
    label: string;
    commands: ChatCommand[];
}

/** Group commands by section, dropping empty sections and hidden/disabled commands.
 *
 *  Default (no query): sections in fixed SECTIONS order; within a section `order`
 *  ascending then A–Z.
 *
 *  `preserveOrder` (a relevance-ranked query result, like VS Code): the incoming
 *  order is kept BOTH within a section AND across sections — a section appears at
 *  the rank of its first matching command. So filtering "usage" floats Slash
 *  Commands (with /usage) above Model (Account & usage), matching VS Code. */
export function groupCommands(commands: ChatCommand[], preserveOrder = false): CommandGroup[] {
    const enabled = commands.filter((c) => c.isEnabled());
    const labelOf = (id: CommandSection) => SECTIONS.find((s) => s.id === id)?.label ?? id;

    if (preserveOrder) {
        const byFirstSeen = new Map<CommandSection, ChatCommand[]>();
        for (const c of enabled) {
            const bucket = byFirstSeen.get(c.section) ?? [];
            bucket.push(c);
            byFirstSeen.set(c.section, bucket);
        }
        return [...byFirstSeen.entries()].map(([id, cmds]) => ({
            section: id,
            label: labelOf(id),
            commands: cmds,
        }));
    }

    return SECTIONS.map(({ id, label }) => ({
        section: id,
        label,
        commands: enabled
            .filter((c) => c.section === id)
            .sort((a, b) => a.order - b.order || a.label.localeCompare(b.label)),
    })).filter((g) => g.commands.length > 0);
}

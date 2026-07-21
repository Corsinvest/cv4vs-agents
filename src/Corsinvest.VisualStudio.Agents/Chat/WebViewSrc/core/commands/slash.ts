/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { ChatCommand, type CommandHost, type CommandSection } from './base';
import { openUsageDialog, openContextDialog } from '../dialog-host';
import { iconForCommandName } from './command-icons';

// When true, the CLI's /usage and /context slash entries are intercepted to open our
// dialog instead of being sent to the CLI. When false (default), the native slash commands
// go straight to the CLI (they print their output in chat) and only our own static commands
// ("Account & usage…", "Context usage…") open the dialogs — one behaviour per command origin.
const REMAP_SLASH_TO_DIALOG = false;

const REMAPPED: Record<string, () => void> = {
    usage: () => openUsageDialog(),
    context: () => openContextDialog(),
};

/**
 * A CLI/skill slash command (`/name`). One instance per entry in the CLI's
 * command list. Running it sends `/name` as a prompt for the CLI to process —
 * except the REMAPPED ones, which open our own dialog. Dynamic (discovered by the
 * CLI), so not in the static registry; the registry builds these at render time.
 */
export class SlashCommand extends ChatCommand {
    readonly section: CommandSection = 'slash';
    override readonly aliases: readonly string[];
    // CLI slash commands arrive from `initialize` without an icon; supply a
    // hand-curated one by name when we have it (e.g. /compact → broom).
    override readonly icon?: string;
    constructor(
        readonly name: string,
        readonly description = '',
        readonly argumentHint = '',
        aliases: readonly string[] = [],
    ) {
        super();
        this.aliases = aliases;
        this.icon = iconForCommandName(name);
    }
    get id(): string {
        return `slash:${this.name}`;
    }
    get label(): string {
        return `/${this.name}`;
    }
    override run(host: CommandHost): void {
        const remap = REMAP_SLASH_TO_DIALOG ? REMAPPED[this.name] : undefined;
        if (remap) {
            remap();
            return;
        }
        // echo: show "/name" as a user bubble (this is a real slash command the user ran).
        host.sendPrompt(`/${this.name}`, true);
    }
}

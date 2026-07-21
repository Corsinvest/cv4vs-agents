/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Small built-in commands — each is a one-line host action. Grouped here since
// they're trivial; richer commands with their own UI (e.g. Usage, and future
// Effort/Thinking/Rewind) stay in their own files.

import Attach16Regular from '@fluentui/svg-icons/icons/attach_16_regular.svg';
import Mention16Regular from '@fluentui/svg-icons/icons/mention_16_regular.svg';
import Broom16Regular from '@fluentui/svg-icons/icons/broom_16_regular.svg';
import History16Regular from '@fluentui/svg-icons/icons/history_16_regular.svg';
import ChatAdd16Regular from '@fluentui/svg-icons/icons/chat_add_16_regular.svg';
import Brain16Regular from '@fluentui/svg-icons/icons/brain_16_regular.svg';
import Shield16Regular from '@fluentui/svg-icons/icons/shield_16_regular.svg';
import Settings16Regular from '@fluentui/svg-icons/icons/settings_16_regular.svg';
import Book16Regular from '@fluentui/svg-icons/icons/book_16_regular.svg';
import Bug16Regular from '@fluentui/svg-icons/icons/bug_16_regular.svg';
import WindowConsole20Regular from '@fluentui/svg-icons/icons/window_console_20_regular.svg';
import PuzzlePiece16Regular from '@fluentui/svg-icons/icons/puzzle_piece_16_regular.svg';
import {
    ChatCommand,
    type CommandHost,
    type CommandSection,
    type CommandTrailing,
    type TrailingControl,
} from './base';
import { state as appState } from '../state';
import { modelLabel } from '../ai-models';
import { permissionItems } from '../permission-modes';
import { LINKS } from './links';

export class AttachFileCommand extends ChatCommand {
    readonly id = 'attach-file';
    readonly label = 'Attach file…';
    readonly description = 'Upload a file to include in the conversation';
    readonly section: CommandSection = 'context';
    readonly order = 10;
    readonly icon = Attach16Regular;
    override readonly aliases = ['upload', 'image', 'file'];
    override run(host: CommandHost): void {
        host.pickFile();
    }
}

export class MentionFileCommand extends ChatCommand {
    readonly id = 'mention-file';
    readonly label = 'Mention file from this project…';
    readonly description = 'Reference a project file with an @mention';
    readonly section: CommandSection = 'context';
    readonly order = 20;
    readonly icon = Mention16Regular;
    override readonly aliases = ['mention', 'reference', 'file'];
    override run(host: CommandHost): void {
        host.insertAtCaret('@');
    }
}

export class ClearCommand extends ChatCommand {
    readonly id = 'clear';
    readonly label = 'Clear conversation';
    readonly description = 'Start a new conversation';
    readonly section: CommandSection = 'context';
    readonly order = 30;
    readonly icon = Broom16Regular;
    override readonly aliases = ['clear', 'reset', 'new'];
    override run(host: CommandHost): void {
        host.sendPrompt('/clear');
    }
}

export class NewConversationCommand extends ChatCommand {
    readonly id = 'new-conversation';
    readonly label = 'New conversation';
    readonly description = 'Open a new conversation in a new pane';
    readonly section: CommandSection = 'context';
    readonly order = 35;
    readonly icon = ChatAdd16Regular;
    // Not 'new': that's an alias of Clear, which restarts the conversation in place.
    override readonly aliases = ['pane', 'tab', 'split'];
    override run(host: CommandHost): void {
        host.openChatPane();
    }
}

export class ResumeConversationCommand extends ChatCommand {
    readonly id = 'resume-conversation';
    readonly label = 'Resume conversation…';
    readonly description = 'Continue a previous conversation';
    readonly section: CommandSection = 'context';
    readonly order = 40;
    readonly icon = History16Regular;
    override readonly aliases = ['resume', 'history', 'sessions', 'continue'];
    override run(host: CommandHost): void {
        host.openSessionHistory();
    }
}

export class SwitchModelCommand extends ChatCommand {
    readonly id = 'model';
    readonly label = 'Switch model…';
    readonly description = 'Change the AI model';
    readonly section: CommandSection = 'model';
    readonly order = 10;
    readonly icon = Brain16Regular;
    readonly trailing: CommandTrailing = 'value';
    override readonly aliases = ['model', 'opus', 'sonnet', 'haiku'];
    // Show the current model name on the right (e.g. "Opus 4.8"), like VS Code.
    override get trailingControl(): TrailingControl {
        return { kind: 'value', label: modelLabel(appState.currentModel) };
    }
    override run(host: CommandHost): void {
        host.openModelPicker();
    }
}

export class SwitchPermissionModeCommand extends ChatCommand {
    readonly id = 'permission-mode';
    readonly label = 'Switch permission mode…';
    readonly description = 'Choose how much Claude may do without asking';
    readonly section: CommandSection = 'model';
    readonly order = 20;
    readonly icon = Shield16Regular;
    readonly trailing: CommandTrailing = 'value';
    override readonly aliases = ['permission', 'permissions', 'mode', 'plan', 'auto'];
    // Current mode on the right, like the model command shows the model name.
    override get trailingControl(): TrailingControl {
        const cur = appState.permissionMode;
        const item = permissionItems().find((it) => it.value === cur);
        return { kind: 'value', label: item?.label ?? cur, icon: item?.icon };
    }
    override run(host: CommandHost): void {
        host.openPermissionPicker();
    }
}

export class GeneralConfigCommand extends ChatCommand {
    readonly id = 'general-config';
    readonly label = 'Settings…';
    readonly description = 'Open the extension options';
    readonly section: CommandSection = 'settings';
    readonly order = 10;
    readonly icon = Settings16Regular;
    override readonly aliases = ['settings', 'config', 'options', 'preferences'];
    override run(host: CommandHost): void {
        host.openOptions();
    }
}

export class ManagePluginsCommand extends ChatCommand {
    readonly id = 'manage-plugins';
    readonly label = 'Manage plugins';
    readonly description = 'Install, enable and manage Claude Code plugins';
    readonly section: CommandSection = 'customize';
    readonly order = 10;
    readonly icon = PuzzlePiece16Regular;
    override readonly aliases = ['plugin', 'plugins', 'extension', 'marketplace'];
    override run(host: CommandHost): void {
        host.openPluginManager();
    }
}

export class OpenCliTerminalCommand extends ChatCommand {
    readonly id = 'open-cli-terminal';
    readonly label = 'Open Claude in Terminal';
    readonly description = 'Open a new interactive CLI session';
    readonly section: CommandSection = 'customize';
    readonly order = 20;
    readonly icon = WindowConsole20Regular;
    override readonly aliases = ['cli', 'console', 'shell', 'terminal'];
    override run(host: CommandHost): void {
        host.openCliTerminal();
    }
}

export class HelpDocsCommand extends ChatCommand {
    readonly id = 'help-docs';
    readonly label = 'View help docs';
    readonly description = 'Open the documentation on GitHub';
    readonly section: CommandSection = 'support';
    readonly order = 10;
    readonly icon = Book16Regular;
    override readonly aliases = ['help', 'docs', 'documentation', 'guide'];
    override run(host: CommandHost): void {
        host.openExternalUrl(LINKS.helpDocs);
    }
}

export class ReportProblemCommand extends ChatCommand {
    readonly id = 'report-problem';
    readonly label = 'Report a problem';
    readonly description = 'Open a new issue on GitHub';
    readonly section: CommandSection = 'support';
    readonly order = 20;
    readonly icon = Bug16Regular;
    override readonly aliases = ['bug', 'issue', 'feedback', 'report'];
    override run(host: CommandHost): void {
        host.openExternalUrl(LINKS.issues);
    }
}

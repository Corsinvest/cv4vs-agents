/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import type { TemplateResult } from 'lit';

/**
 * Chat command model — the `/` menu and the lightning action menu are built from
 * these. Same shape as the tool-renderers: a command is a small class that gets a
 * CommandHost (the atomic actions it may call) and stays pure — no bridge/state/DOM
 * imports. Mirrors the CLI's kinds: `run` acts and is done (CLI `local`); `render`
 * opens its own result UI (CLI `local-jsx`); dynamic CLI/skill commands are `run`
 * commands that just `host.sendPrompt("/name args")` (CLI `prompt`).
 *
 * One file per command under this folder; the registry collects + orders them.
 */

/** Sections group commands in the menu (VS Code-style headings), shown in the
 *  fixed order declared by SECTIONS in the registry. Mirrors VS Code's menu:
 *  Context · Model · Customize · Slash Commands · Settings · Support. A section
 *  with no commands is not drawn. */
export type CommandSection = 'context' | 'model' | 'customize' | 'slash' | 'settings' | 'support';

/** Inline control shown on the right of a menu row (toggle/slider/value). */
export type CommandTrailing = 'toggle' | 'slider' | 'value';

/**
 * Atomic actions a command may invoke, implemented by the composer (cv-prompt).
 * Kept minimal and semantic — same spirit as ToolHost. If it grows a lot, a
 * command is probably doing too much.
 */
export interface CommandHost {
    /** Send a prompt to the CLI. Used for builtins routed to the CLI ("/clear",
     *  "/compact") and for dynamic prompt-commands ("/name args"). When `echo` is
     *  true the "/name" is also shown as a user bubble (slash commands picked from the
     *  menu, matching the VS Code webview); builtins with a friendly label leave it off. */
    sendPrompt(text: string, echo?: boolean): void;
    /** Insert text at the caret in the prompt box (e.g. "@" or "/name "). */
    insertAtCaret(text: string): void;
    /** Open the upload-from-computer file picker. */
    pickFile(): void;
    /** Open the model selector popover. */
    openModelPicker(): void;
    /** Open the permission-mode picker. */
    openPermissionPicker(): void;
    /** Open a URL in the system browser (help docs, issue tracker). */
    openExternalUrl(url: string): void;
    /** Open the extension's Tools → Options page (General config). */
    openOptions(): void;
    /** Open a fresh interactive CLI pane (same as the toolbar "+" for CLI). */
    openCliTerminal(): void;
    /** Open this pane's session picker (same popup as the toolbar History button). */
    openSessionHistory(): void;
    /** Open a fresh chat pane (same as the toolbar "+" for Chat). */
    openChatPane(): void;
    /** Open the Manage Plugins dialog. */
    openPluginManager(): void;
    /** Merge keys into the CLI flag-settings layer (effortLevel,
     *  alwaysThinkingEnabled, fastMode) for the Model menu controls. */
    applyFlagSettings(settings: Record<string, unknown>): void;
    /** Hot-swap the runtime thinking budget (Thinking toggle). display is
     *  null to disable. */
    setMaxThinkingTokens(maxThinkingTokens: number, display: string | null): void;
}

/**
 * Inline trailing control data (no Lit) so commands stay DOM-free: the menu maps
 * each `kind` to a generic renderer. New control = a variant here + a menu case.
 */
export type TrailingControl =
    | { kind: 'toggle'; on: boolean }
    /** `icon` (raw SVG) shows the current value's own glyph next to the label — used where the
     *  value has one, so the row, the toolbar trigger and the picker all read the same. */
    | { kind: 'value'; label: string; icon?: string }
    | {
          kind: 'slider';
          stops: ReadonlyArray<{ label: string; value: number; accent?: boolean }>;
          value: number;
          label: string;
          onSet: (host: CommandHost, value: number) => void;
      };

/**
 * Base class for a chat command. Concrete commands override `run` (action) or
 * `render` (result UI). Defaults are no-ops so a command can implement just one.
 */
export abstract class ChatCommand {
    abstract readonly id: string;
    abstract readonly label: string;
    abstract readonly section: CommandSection;
    /** Position within its section (ascending). Ties break by label A–Z, so
     *  unset (0) items sort alphabetically — matching the CLI slash list. */
    readonly order: number = 0;
    readonly description?: string;
    readonly aliases: readonly string[] = [];
    readonly icon?: string;
    /** Inline control on the right of the row (toggle/slider/value). */
    readonly trailing?: CommandTrailing;
    /** On state for `trailing: 'toggle'` commands (drives the switch). */
    get checked(): boolean {
        return false;
    }
    /** Trailing control for the menu; default builds a toggle from `checked`. */
    get trailingControl(): TrailingControl | null {
        return this.trailing === 'toggle' ? { kind: 'toggle', on: this.checked } : null;
    }
    /** Keep the menu open after selecting (toggles/sliders). */
    readonly keepMenuOpen?: boolean;

    /** Hidden when this returns false (capability gate). Default: always shown. */
    isEnabled(): boolean {
        return true;
    }

    /** Action: do something via the host, then the menu closes. */
    run(_host: CommandHost): void {
        // no-op by default — render-only commands don't act
    }

    /** Result UI: render the command's own panel (e.g. Usage). Undefined = none. */
    render?(host: CommandHost): TemplateResult;
}

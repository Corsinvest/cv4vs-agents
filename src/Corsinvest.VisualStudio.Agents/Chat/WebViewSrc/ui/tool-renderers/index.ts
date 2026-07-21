/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Tool renderer dispatcher. makeRenderer(name, host) constructs the renderer
// for a tool, falling back to the MCP renderer for mcp__* tools and the
// catch-all default for anything else. Same idea as the VS Code extension's
// b0(toolName) dispatcher.

import { html, type TemplateResult } from 'lit';
import { ToolRenderer } from './base';
import type { ToolHost } from './types';
import {
    ReadRenderer,
    EditRenderer,
    WriteRenderer,
    MultiEditRenderer,
    NotebookEditRenderer,
    GrepRenderer,
    GlobRenderer,
    WebSearchRenderer,
    ShellRenderer,
    PowerShellRenderer,
    WebFetchRenderer,
    AgentRenderer,
    SkillRenderer,
    ToolSearchRenderer,
    EnterWorktreeRenderer,
    ExitWorktreeRenderer,
    BashOutputRenderer,
    EnterPlanModeRenderer,
    ExitPlanModeRenderer,
    KillShellRenderer,
    ReadMcpResourceRenderer,
    TodoWriteRenderer,
    AskUserQuestionRenderer,
} from './renderers';

/** Catch-all for unknown tools: best-effort header + standard IN/OUT body. */
class DefaultToolRenderer extends ToolRenderer {
    readonly name = '';
}

/** mcp__server__tool: header "Server [tool]" + standard IN/OUT body. */
class McpToolRenderer extends ToolRenderer {
    readonly name = '';
    override header(): TemplateResult {
        const parts = this.host.name.slice('mcp__'.length).split('__');
        const server = parts[0] ?? '';
        const tool = parts.slice(1).join('__');
        const human = server.charAt(0).toUpperCase() + server.slice(1);
        const label = tool ? `${human} [${tool}]` : human;
        return html`${this.nameSpan(label)}${this.detailSpan(this.detailText())}`;
    }
}

type RendererCtor = new (host: ToolHost) => ToolRenderer;

// Tool name → renderer class. Add a tool = add one line here + its class.
const BY_NAME: Record<string, RendererCtor> = {
    Read: ReadRenderer,
    Edit: EditRenderer,
    Write: WriteRenderer,
    MultiEdit: MultiEditRenderer,
    NotebookEdit: NotebookEditRenderer,
    Grep: GrepRenderer,
    Glob: GlobRenderer,
    WebSearch: WebSearchRenderer,
    Bash: ShellRenderer,
    PowerShell: PowerShellRenderer,
    WebFetch: WebFetchRenderer,
    Agent: AgentRenderer,
    Skill: SkillRenderer,
    ToolSearch: ToolSearchRenderer,
    EnterWorktree: EnterWorktreeRenderer,
    ExitWorktree: ExitWorktreeRenderer,
    BashOutput: BashOutputRenderer,
    EnterPlanMode: EnterPlanModeRenderer,
    ExitPlanMode: ExitPlanModeRenderer,
    KillShell: KillShellRenderer,
    ReadMcpResource: ReadMcpResourceRenderer,
    TodoWrite: TodoWriteRenderer,
    AskUserQuestion: AskUserQuestionRenderer,
};

export function makeRenderer(name: string | undefined | null, host: ToolHost): ToolRenderer {
    if (!name) {
        return new DefaultToolRenderer(host);
    }
    const Ctor =
        BY_NAME[name] ?? (name.startsWith('mcp__') ? McpToolRenderer : DefaultToolRenderer);
    return new Ctor(host);
}

export { ToolRenderer } from './base';
export type { ToolHost } from './types';

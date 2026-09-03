/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Tool renderer dispatcher. makeRenderer(name, host) constructs the renderer
// for a tool, falling back to the MCP renderer for mcp__* tools and the
// catch-all default for anything else. Same idea as the VS Code extension's
// b0(toolName) dispatcher.

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
    TaskOutputRenderer,
    TaskCreateRenderer,
    TaskUpdateRenderer,
    TaskGetRenderer,
    TaskListRenderer,
    TaskStopRenderer,
    TeamCreateRenderer,
    TeamDeleteRenderer,
    SendMessageRenderer,
    BriefRenderer,
    CronCreateRenderer,
    CronDeleteRenderer,
    CronListRenderer,
    SleepRenderer,
    RemoteTriggerRenderer,
    ConfigRenderer,
    LspRenderer,
    ReplRenderer,
    EnterPlanModeRenderer,
    ExitPlanModeRenderer,
    KillShellRenderer,
    ReadMcpResourceRenderer,
    TodoWriteRenderer,
    AskUserQuestionRenderer,
    DefaultToolRenderer,
    McpToolRenderer,
} from './renderers';

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
    TaskOutput: TaskOutputRenderer,
    TaskCreate: TaskCreateRenderer,
    TaskUpdate: TaskUpdateRenderer,
    TaskGet: TaskGetRenderer,
    TaskList: TaskListRenderer,
    TaskStop: TaskStopRenderer,
    TeamCreate: TeamCreateRenderer,
    TeamDelete: TeamDeleteRenderer,
    SendMessage: SendMessageRenderer,
    // Brief goes out under its older name; both appear across transcripts.
    Brief: BriefRenderer,
    SendUserMessage: BriefRenderer,
    CronCreate: CronCreateRenderer,
    CronDelete: CronDeleteRenderer,
    CronList: CronListRenderer,
    Sleep: SleepRenderer,
    RemoteTrigger: RemoteTriggerRenderer,
    Config: ConfigRenderer,
    LSP: LspRenderer,
    REPL: ReplRenderer,
    EnterPlanMode: EnterPlanModeRenderer,
    ExitPlanMode: ExitPlanModeRenderer,
    KillShell: KillShellRenderer,
    // The CLI's own name carries the suffix; the bare one only ever appears in old transcripts.
    ReadMcpResourceTool: ReadMcpResourceRenderer,
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

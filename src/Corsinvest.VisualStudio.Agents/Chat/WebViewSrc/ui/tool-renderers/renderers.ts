/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// One class per tool. Each overrides only what differs — row() picks a layout,
// header()/body() supply content. Adding a tool is a single class here,
// registered in index.ts. No name-switching anywhere else.

import { html, nothing, type TemplateResult } from 'lit';
import { displayPath, fileName } from '../../core/path';
import { truncate } from '../helpers/format';
import { ToolRenderer } from './base';
import { state as appState } from '../../core/state';
import { formatElapsed } from './tool-host';
import type { AskQuestion } from '../../core/types';

interface TodoItem {
    content?: string;
    status?: 'pending' | 'in_progress' | 'completed' | string;
}

export class ReadRenderer extends ToolRenderer {
    readonly name = 'Read';
    override row(): TemplateResult {
        return this.rowHeaderOnly();
    }
    override header(): TemplateResult {
        const i = this.host.input;
        const fp = String(i.file_path ?? '');
        const offset = i.offset != null ? Number(i.offset) : null;
        const limit = i.limit != null ? Number(i.limit) : null;
        const range =
            offset != null && limit != null
                ? ` (lines ${offset + 1}-${offset + limit})`
                : offset != null
                  ? ` (from line ${offset + 1})`
                  : '';
        const start = offset != null ? offset + 1 : 0;
        const end = start > 0 && limit != null && limit > 0 ? start + limit - 1 : start;
        const link = this.fileLink(
            fp,
            html`${displayPath(fp, this.host.workingDirectory, appState.ui.showRelativePaths)}${range}`,
            start,
            end,
        );
        return html`${this.nameSpan('Read')}${this.detailSpan(link)}`;
    }
}

export class EditRenderer extends ToolRenderer {
    readonly name: string = 'Edit';
    override label(): string {
        return 'Edit';
    }
    override row(): TemplateResult {
        return this.rowDiff();
    }
    protected override renderHeaderActions(): TemplateResult | typeof nothing {
        return this.diffActionButtons();
    }
    override header(): TemplateResult {
        const fp = String(this.host.input.file_path ?? '');
        const link = this.editFileLink(
            fp,
            html`${displayPath(fp, this.host.workingDirectory, appState.ui.showRelativePaths)}`,
        );
        return html`${this.nameSpan(this.label())}${this.detailSpan(link)}`;
    }
}

export class WriteRenderer extends EditRenderer {
    override readonly name = 'Write';
    override label(): string {
        return 'Write';
    }
}

export class MultiEditRenderer extends EditRenderer {
    override readonly name = 'MultiEdit';
}

export class NotebookEditRenderer extends ToolRenderer {
    readonly name = 'NotebookEdit';
    override header(): TemplateResult {
        const fp = String(this.host.input.notebook_path ?? this.host.input.file_path ?? '');
        return html`${this.nameSpan('NotebookEdit')}${this.detailSpan(
            this.fileLink(fp, html`${fileName(fp)}`),
        )}`;
    }
    override inputText(): string {
        return String(this.host.input.new_source ?? '');
    }
}

export class GrepRenderer extends ToolRenderer {
    readonly name = 'Grep';
    override row(): TemplateResult {
        return this.rowCount('matches', 'No matches');
    }
    override header(): TemplateResult {
        const i = this.host.input;
        const pat = String(i.pattern ?? i.query ?? '');
        const extras: string[] = [];
        // Shorten the search path relative to the workdir, exactly like Edit/Read.
        if (i.path) {
            extras.push(
                `in ${displayPath(String(i.path), this.host.workingDirectory, appState.ui.showRelativePaths)}`,
            );
        }
        if (i.glob) {
            extras.push(`glob: ${String(i.glob)}`);
        }
        if (i.type) {
            extras.push(`type: ${String(i.type)}`);
        }
        const text = (pat ? `"${pat}"` : '') + (extras.length ? ` (${extras.join(', ')})` : '');
        return html`${this.nameSpan('Grep')}${this.detailSpan(text)}`;
    }
}

export class GlobRenderer extends ToolRenderer {
    readonly name = 'Glob';
    override row(): TemplateResult {
        return this.rowCount('files', 'No files');
    }
    override header(): TemplateResult {
        const pat = String(this.host.input.pattern ?? '');
        return html`${this.nameSpan('Glob')}${this.detailSpan(pat ? `pattern: "${pat}"` : '')}`;
    }
}

export class WebSearchRenderer extends ToolRenderer {
    readonly name = 'WebSearch';
    override row(): TemplateResult {
        return this.rowCount('results', 'No results');
    }
    override header(): TemplateResult {
        const q = String(this.host.input.query ?? '');
        const detail = q
            ? this.urlLink(
                  `https://www.google.com/search?q=${encodeURIComponent(q)}`,
                  truncate(q, 80),
              )
            : '';
        return html`${this.nameSpan('Web Search')}${this.detailSpan(detail)}`;
    }
}

export class ShellRenderer extends ToolRenderer {
    readonly name: string = 'Bash';
    constructor(host: ConstructorParameters<typeof ToolRenderer>[0]) {
        super(host);
        this.host.clipsOutput = true;
    }
    override header(): TemplateResult {
        const i = this.host.input;
        const cmd = String(i.command ?? i.script ?? i.code ?? '');
        const desc = String(i.description ?? '');
        return html`${this.nameSpan(this.host.name)}${this.detailSpan(desc || truncate(cmd, 60))}`;
    }
    override inputText(): string {
        const i = this.host.input;
        return String(i.command ?? i.script ?? i.code ?? '');
    }
}

export class PowerShellRenderer extends ShellRenderer {
    override readonly name = 'PowerShell';
}

export class WebFetchRenderer extends ToolRenderer {
    readonly name = 'WebFetch';
    override header(): TemplateResult {
        const url = String(this.host.input.url ?? '');
        const text = truncate(url, 80);
        const detail = /^https?:\/\//.test(url) ? this.urlLink(url, text) : text;
        return html`${this.nameSpan('Web Fetch')}${this.detailSpan(detail)}`;
    }
    protected override autoOpen(): boolean {
        return true;
    }
    override inputText(): string {
        return String(this.host.input.prompt ?? '');
    }
}

export class AgentRenderer extends ToolRenderer {
    readonly name = 'Agent';
    /** The active sub-agent task backing this row, if it's still running. */
    private _activeTask() {
        return appState.subagentTasks.find((t) => t.toolUseId === this.host.toolUseId);
    }
    // The Agent tool_result is just launch metadata and arrives immediately, so the
    // row would flip to "done" at once. Keep it running while its sub-agent task is
    // live, so the dot keeps spinning for the whole run.
    override isRunning(): boolean {
        return this.host.status === 'pending' || this._activeTask() != null;
    }
    override header(): TemplateResult {
        const desc = truncate(String(this.host.input.description ?? ''), 80);
        // Elapsed time while the sub-agent runs (the dot handles the spinner).
        const active = this._activeTask();
        const badge = active
            ? html`<span class="cv-agent-time"
                  >${formatElapsed(active.usage.durationMs / 1000)}</span
              >`
            : nothing;
        return html`${this.nameSpan('Agent')}${this.detailSpan(desc)}${badge}`;
    }
    // IN = the sub-agent prompt (like the VS Code extension). The tool's own
    // result is just launch metadata ("Async agent launched… do not mention"),
    // so there is no OUT — the real work shows as the nested sub-agent rows below.
    override inputText(): string {
        return String(
            this.host.input.prompt ?? this.host.input.message ?? this.host.input.description ?? '',
        );
    }
    override body(): TemplateResult | null {
        const inText = this.inputText();
        return inText ? this.ioGrid(inText, false) : null;
    }
    // A sub-agent's whole transcript (prompt IN + nested rows) is a lot of content, so the
    // row starts collapsed even when previews are on — dot + description until expanded — and
    // keeps its chevron visible at rest so it reads as expandable. A click toggles it like
    // any other tool. This is the only tool that opts into it.
    override defaultCollapsed(): boolean {
        return true;
    }
    // The chevron must appear while the sub-agent runs, not only once it finishes — so the row can be
    // opened to follow the live children. Expandable when there's a prompt body OR any child yet.
    protected override hasExpandableContent(): boolean {
        // An Agent is always expandable: it has a prompt body, and even at 0 children the chevron must
        // show so expanding can kick the lazy preview fetch (history). agentId makes that explicit.
        return this.body() !== null || this.host.agentId !== '' || this.host.childCount > 0;
    }
    protected override renderHeaderActions(): TemplateResult | typeof nothing {
        // Copy-output + show-all only make sense once the transcript is open (same as before, when the
        // slot was gated on `open`). Collapsed → just the error button, if any.
        return this.host.expanded
            ? this.host.componentHeaderActions()
            : super.renderHeaderActions();
    }
}

export class SkillRenderer extends ToolRenderer {
    readonly name = 'Skill';
    override header(): TemplateResult {
        return html`${this.nameSpan('Skill')}${this.detailSpan(
            String(this.host.input.name ?? this.host.input.skill ?? ''),
        )}`;
    }
    override inputText(): string {
        return String(this.host.input.args ?? '');
    }
}

abstract class HeaderOnlyRenderer extends ToolRenderer {
    override row(): TemplateResult {
        return this.rowHeaderOnly();
    }
}

export class ToolSearchRenderer extends HeaderOnlyRenderer {
    readonly name = 'ToolSearch';
    override header(): TemplateResult {
        const q = String(this.host.input.query ?? '');
        return html`${this.nameSpan('Search tools')}${this.detailSpan(q ? `"${q}"` : '')}`;
    }
}

export class EnterWorktreeRenderer extends HeaderOnlyRenderer {
    readonly name = 'EnterWorktree';
    override header(): TemplateResult {
        return html`${this.nameSpan('Enter Worktree')}${this.detailSpan(
            String(this.host.input.name ?? ''),
        )}`;
    }
}

export class ExitWorktreeRenderer extends HeaderOnlyRenderer {
    readonly name = 'ExitWorktree';
    override header(): TemplateResult {
        return html`${this.nameSpan('Exit Worktree')}${this.detailSpan(
            String(this.host.input.action ?? ''),
        )}`;
    }
}

export class BashOutputRenderer extends HeaderOnlyRenderer {
    readonly name = 'BashOutput';
    override header(): TemplateResult {
        return html`${this.nameSpan('Bash Output')}${this.detailSpan(
            String(this.host.input.bash_id ?? ''),
        )}`;
    }
}

export class EnterPlanModeRenderer extends HeaderOnlyRenderer {
    readonly name = 'EnterPlanMode';
    override label(): string {
        return 'Plan Mode';
    }
    override header(): TemplateResult {
        return html`${this.nameSpan('Plan Mode')}`;
    }
}

export class ExitPlanModeRenderer extends HeaderOnlyRenderer {
    readonly name = 'ExitPlanMode';
    override header(): TemplateResult {
        return html`${this.nameSpan('ExitPlanMode')}`;
    }
}

export class KillShellRenderer extends HeaderOnlyRenderer {
    readonly name = 'KillShell';
    override header(): TemplateResult {
        return html`${this.nameSpan('Kill Shell')}`;
    }
}

export class ReadMcpResourceRenderer extends HeaderOnlyRenderer {
    readonly name = 'ReadMcpResource';
    override header(): TemplateResult {
        return html`${this.nameSpan('ReadMcpResource')}`;
    }
}

export class TodoWriteRenderer extends ToolRenderer {
    readonly name = 'TodoWrite';
    override row(): TemplateResult {
        return this.rowCustom(this.todoBody());
    }
    override header(): TemplateResult {
        return html`${this.nameSpan('Update Todos')}`;
    }
    private todoBody(): TemplateResult {
        const todos = ((this.host.input ?? {}) as { todos?: TodoItem[] }).todos ?? [];
        if (!todos.length) {
            return html`${nothing}`;
        }
        return html`
            <div class="cv-tool-body">
                <div class="cv-todo-list">
                    ${todos.map((t) => {
                        const done = t.status === 'completed';
                        const inProgress = t.status === 'in_progress';
                        const icon = done ? '✓' : inProgress ? '›' : '○';
                        const cls = done
                            ? 'cv-todo-done'
                            : inProgress
                              ? 'cv-todo-progress'
                              : 'cv-todo-pending';
                        return html`<div class="cv-todo-item ${cls}">
                            <span class="cv-todo-icon">${icon}</span>
                            <span class="cv-todo-text">${t.content ?? ''}</span>
                        </div>`;
                    })}
                </div>
            </div>
        `;
    }
}

export class AskUserQuestionRenderer extends ToolRenderer {
    readonly name = 'AskUserQuestion';
    override row(): TemplateResult {
        return this.rowCustom(this.questionsBody());
    }
    override header(): TemplateResult {
        const qs = (this.host.input.questions ?? []) as AskQuestion[];
        const first = qs[0]?.question ?? '';
        const more = qs.length > 1 ? ` (+${qs.length - 1})` : '';
        return html`${this.nameSpan('Ask')}${this.detailSpan(`${truncate(first, 80)}${more}`)}`;
    }
    /** Compact answered view: one line per question — the header chip (or the
     *  truncated question text) followed by the chosen option(s). Mirrors VS
     *  Code's terse summary, dropping the options the user didn't pick. */
    private compactBody(questions: AskQuestion[], answered: string): TemplateResult {
        return html`
            <div class="cv-tool-body">
                ${this.answerCopy(questions, answered)}
                <div class="cv-question-list cv-question-compact">
                    ${questions.map((q) => {
                        const chosen = chosenLabels(q, answered);
                        return html`<div class="cv-question-answer">
                            ${
                                q.header
                                    ? html`<span class="cv-question-chip">${q.header}</span>`
                                    : html`<span class="cv-question-text"
                                          >${truncate(q.question ?? '', 60)}</span
                                      >`
                            }
                            <span class="cv-question-answer-val"
                                >${chosen.length ? chosen.join(', ') : '—'}</span
                            >
                        </div>`;
                    })}
                </div>
            </div>
        `;
    }

    /** Copy button for the Ask body. Copies markdown built from the questions —
     *  matching the shown view: compact = "- **Header**: chosen" per line; full =
     *  "**N. Header**" + one bullet per option, a ✅ prefixing the chosen ones (the
     *  unchosen have no marker). NOT the CLI's raw "Your questions have been
     *  answered: …" result. Only shown once answered. */
    private answerCopy(questions: AskQuestion[], answered: string): TemplateResult {
        if (!answered) {
            return html`${nothing}`;
        }
        const title = (q: AskQuestion): string => q.header || q.question || '';
        const md = appState.ui.compactOutputAskAnswers
            ? questions
                  .map((q) => `- **${title(q)}**: ${chosenLabels(q, answered).join(', ') || '—'}`)
                  .join('\n')
            : questions
                  .map((q, i) => {
                      const opts = (q.options ?? [])
                          .map((o) => {
                              const label = o.label ?? '';
                              const mark = isChosen(label, answered) ? '✅ ' : '';
                              const desc = o.description ? ` — ${o.description}` : '';
                              return `- ${mark}${label}${desc}`;
                          })
                          .join('\n');
                      return `**${i + 1}. ${title(q)}**\n\n${opts}`;
                  })
                  .join('\n\n');
        return html`<cv-copy-btn
            class="cv-question-copy"
            .text=${md}
            title="Copy answer"
        ></cv-copy-btn>`;
    }

    private questionsBody(): TemplateResult {
        const questions =
            ((this.host.input ?? {}) as { questions?: AskQuestion[] }).questions ?? [];
        if (!questions.length) {
            return html`${nothing}`;
        }
        const answered = cleanText(this.host.result);
        // Compact (VS Code style): once answered, show only the chosen option per
        // question. While still pending (no result yet) fall through to the full
        // list so all options are visible.
        if (appState.ui.compactOutputAskAnswers && answered) {
            return this.compactBody(questions, answered);
        }
        return html`
            <div class="cv-tool-body">
                ${this.answerCopy(questions, answered)}
                <div class="cv-question-list">
                    ${questions.map((q) => {
                        const opts = q.options ?? [];
                        return html`<div class="cv-question">
                            <div class="cv-question-head">
                                ${
                                    q.header
                                        ? html`<span class="cv-question-chip">${q.header}</span>`
                                        : nothing
                                }
                                <span class="cv-question-text">${q.question ?? ''}</span>
                            </div>
                            ${opts.map((o) => {
                                const label = o.label ?? '';
                                const chosen = isChosen(label, answered);
                                return html`<div class="cv-question-opt ${chosen ? 'chosen' : ''}">
                                    <span class="cv-question-opt-mark">${chosen ? '●' : '○'}</span>
                                    <span class="cv-question-opt-text">
                                        <span class="cv-question-opt-label">${label}</span>
                                        ${
                                            o.description
                                                ? html`<span class="cv-question-opt-desc"
                                                      >${o.description}</span
                                                  >`
                                                : nothing
                                        }
                                    </span>
                                </div>`;
                            })}
                        </div>`;
                    })}
                </div>
            </div>
        `;
    }
}

function cleanText(s: string): string {
    return (s ?? '').replace(/\s+$/, '');
}

/** True if `label` appears in the CLI's answered-result text. The answer isn't
 *  structured, so membership is a substring match (as VS Code does). */
function isChosen(label: string, answered: string): boolean {
    return !!label && !!answered && answered.includes(label);
}

/** The option labels chosen for a question, per {@link isChosen}. */
function chosenLabels(q: AskQuestion, answered: string): string[] {
    return (q.options ?? []).map((o) => o.label ?? '').filter((l) => isChosen(l, answered));
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// One class per tool. Each overrides only what differs — row() picks a layout,
// header()/body() supply content. Adding a tool is a single class here,
// registered in index.ts. No name-switching anywhere else.

import { html, nothing, type TemplateResult } from 'lit';
import { fileName } from '../../core/path';
import { displayPathUi } from '../paths';
import { truncate } from '../helpers/format';
import { ToolRenderer, type ClipMode } from './base';
import { state as appState } from '../../core/state';
import { formatDuration, formatTokens } from '../helpers/format';
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
        const link = this.fileLink(fp, html`${displayPathUi(fp)}${range}`, start, end);
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
    override header(): TemplateResult {
        const fp = String(this.host.input.file_path ?? '');
        const oldS = String(this.host.input.old_string ?? '');
        const newS = String(this.host.input.new_string ?? '');
        // Counts trail the path inside the link's own span so they follow its last line when a
        // long path wraps, instead of sitting on a line of their own below the row.
        const link = this.editFileLink(
            fp,
            html`${displayPathUi(fp)} ${this.diffSummary(oldS, newS)}`,
        );
        return html`${this.nameSpan(this.label())}${this.detailSpan(link)}`;
    }
}

/** Write creates a file, so there is no "before" to diff against: rendered as the plain content,
 *  not as a diff where every line is an addition. Deliberately NOT extending EditRenderer — it
 *  would bring rowDiff(), which has nothing to compare here.
 *  Same split VS Code makes (Edit → diff component, Write → the content). */
export class WriteRenderer extends ToolRenderer {
    readonly name = 'Write';
    override label(): string {
        return 'Write';
    }
    override header(): TemplateResult {
        const fp = String(this.host.input.file_path ?? '');
        const content = this.inputText();
        // Size in the header, like Read's line range: tells a 5-line file from a 500-line one
        // without expanding the row.
        const lines = content ? ` (${content.split('\n').length} lines)` : '';
        return html`${this.nameSpan('Write')}${this.detailSpan(
            this.editFileLink(fp, html`${displayPathUi(fp)}${lines}`),
        )}`;
    }
    /** The file's content, not the raw input JSON the base class would print. */
    override inputText(): string {
        return String(this.host.input.content ?? '');
    }
    /** No IN label: there is no in/out pair to tell apart, the body IS the file. And on success
     *  the result only repeats the path already in the header ("File created successfully at: …"),
     *  so it is dropped too — an error still gets its row, being the one thing the header can't say.
     *  The body is a file, so it is highlighted as one, by its own extension. */
    override body(): TemplateResult | null {
        return this.ioGrid(this.inputText(), {
            showOut: this.host.status === 'error',
            inLabel: '',
            highlightAs: this.highlightAs(),
        });
    }
    override highlightAs(): string {
        const name =
            String(this.host.input.file_path ?? '')
                .split(/[\\/]/)
                .pop() ?? '';
        const dot = name.lastIndexOf('.');
        // Extension only when there is one: an extensionless name is not its own language.
        return dot > 0 ? name.slice(dot + 1) : '';
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
            extras.push(`in ${displayPathUi(String(i.path))}`);
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
    /** A command is code, and the thing that gets re-read most — pipes, redirections, quoting.
     *  Both shells are hljs natives, so the language is what tells the two renderers apart. */
    override highlightAs(): string {
        return 'bash';
    }
    /** The command is never clipped, unlike the output it produces: half a pipeline says nothing,
     *  where the first lines of a log still do. Cheap in practice — half the commands are one line. */
    protected override clipsInput(): ClipMode {
        return 'never';
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
    override highlightAs(): string {
        return 'powershell';
    }
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
        // Elapsed time while the sub-agent runs (the dot handles the spinner). cv-elapsed owns the
        // 1s tick — a renderer is rebuilt per render and could not hold a timer.
        const active = this._activeTask();
        // Running: our own clock. Finished: the CLI's totals, which are the authoritative figures
        // (they measure the run, not when the WebView noticed it) and survive into history. An
        // interrupted run reports neither, so it keeps no badge at all.
        const done = this.host.agentTotals;
        const badge = active
            ? html`<cv-elapsed .startedAt=${active.startedAt ?? 0}></cv-elapsed>`
            : done
              ? html`<span class="cv-agent-time cv-agent-totals"
                    ><span class="v">${formatDuration(done.durationMs)}</span> ·
                    <span class="v">${formatTokens(done.tokens)}</span> tok ·
                    <span class="v">${done.toolUses}</span>
                    ${done.toolUses === 1 ? 'tool' : 'tools'}</span
                >`
              : nothing;
        return html`${this.nameSpan('Agent')}${this.detailSpan(desc)}${badge}`;
    }
    // IN = the sub-agent prompt (like the VS Code extension).
    override inputText(): string {
        return String(
            this.host.input.prompt ?? this.host.input.message ?? this.host.input.description ?? '',
        );
    }
    // IN is the prompt we handed the sub-agent, OUT the report it handed back — prose on both
    // sides, so they render as markdown and in full. Keeping the pair on the row itself puts the
    // answer next to the question, where it is readable without scrolling past the nested rows.
    override body(): TemplateResult | null {
        const inText = this.inputText();
        return inText ? this.ioGrid(inText, { markdown: true }) : null;
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
        // Show-all only makes sense once the transcript is open. The error button isn't ours to
        // render: headerActions() adds it on a failed row whatever we return here.
        return this.host.expanded ? this.host.componentHeaderActions() : nothing;
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
    /** The list is the whole content — no chevron, there is no second view to expand into. */
    override row(): TemplateResult {
        const done = this.host.status !== 'pending';
        return this.chrome({
            body: done ? this.todoBody() : null,
            open: done,
            onClick: null,
            chevron: false,
        });
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
    /** Same shape as TodoWrite: the questions body is what there is, so no chevron. */
    override row(): TemplateResult {
        const done = this.host.status !== 'pending';
        return this.chrome({
            body: done ? this.questionsBody() : null,
            open: done,
            onClick: null,
            chevron: false,
        });
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
                        const chosen = answerText(q, answered);
                        return html`<div class="cv-question-answer">
                            ${
                                q.header
                                    ? html`<span class="cv-question-chip">${q.header}</span>`
                                    : html`<span class="cv-question-text"
                                          >${truncate(q.question ?? '', 60)}</span
                                      >`
                            }
                            <span class="cv-question-answer-val">${chosen}</span>
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
            ? questions.map((q) => `- **${title(q)}**: ${answerText(q, answered)}`).join('\n')
            : questions
                  .map((q, i) => {
                      const chosen = chosenLabels(q, answered);
                      const rows = (q.options ?? []).map((o) => {
                          const label = o.label ?? '';
                          const mark = chosen.includes(label) ? '✅ ' : '';
                          const desc = o.description ? ` — ${o.description}` : '';
                          return `- ${mark}${label}${desc}`;
                      });
                      // Typed into "Other": no declared option carries it, so it needs a row of
                      // its own or the copied markdown lists the choices with none of them ticked.
                      const free = chosen.length ? '' : questionAnswer(q, answered);
                      if (free) {
                          rows.push(`- ✅ ${free}`);
                      }
                      return `**${i + 1}. ${title(q)}**\n\n${rows.join('\n')}`;
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
                        // Once per question, not once per option: every option would otherwise
                        // re-run the same regex over the whole result text.
                        const chosen = chosenLabels(q, answered);
                        const free = chosen.length ? '' : questionAnswer(q, answered);
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
                                const isPicked = chosen.includes(label);
                                return html`<div
                                    class="cv-question-opt ${isPicked ? 'chosen' : ''}"
                                >
                                    <span class="cv-question-opt-mark"
                                        >${isPicked ? '●' : '○'}</span
                                    >
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
                            ${
                                // A free-text answer matches none of the options above, so without
                                // this the question renders as if it had gone unanswered.
                                free
                                    ? html`<div class="cv-question-opt chosen">
                                          <span class="cv-question-opt-mark">●</span>
                                          <span class="cv-question-opt-text">
                                              <span class="cv-question-opt-label">${free}</span>
                                          </span>
                                      </div>`
                                    : nothing
                            }
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

/**
 * The option labels chosen for `q`.
 *
 * Matched against this question's own answer — the `"<question>"="<answer>"` pair — and not
 * against the whole result text. Two things go wrong when the blob is searched instead: one
 * option's label can sit inside a longer one ("Tab" inside "Tab to indent, spaces to align"),
 * and with several questions the labels of one can appear in another's answer. Either way more
 * than one row comes back marked chosen for a single-answer question.
 *
 * Within that answer, membership is still a substring test: multi-select answers arrive as one
 * string with the labels run together, so there is no separator to split on.
 */
function chosenLabels(q: AskQuestion, answered: string): string[] {
    const mine = questionAnswer(q, answered);
    if (!mine) {
        return [];
    }
    return (q.options ?? []).map((o) => o.label ?? '').filter((l) => !!l && mine.includes(l));
}

/**
 * The answer the user gave to `q` — a declared option's label or free text alike — or '' when it
 * cannot be told apart.
 *
 * The CLI reports answers as prose wrapping `"<question>"="<answer>"` pairs, followed by
 * instructions addressed to the model. Keying on the question text is what makes this safe with
 * several questions at once: each one picks its own pair instead of the whole blob, which would
 * otherwise put another question's answer under this header.
 *
 * Returns '' when the shape isn't there — a CLI that words this differently gets an em dash, not
 * a paragraph of its own prose rendered as if the user had typed it.
 */
function questionAnswer(q: AskQuestion, answered: string): string {
    const question = q.question ?? '';
    if (!question || !answered) {
        return '';
    }
    const escaped = question.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const m = new RegExp(`"${escaped}"\\s*=\\s*"([^"]*)"`).exec(answered);
    return m ? m[1].trim() : '';
}

/**
 * What to show as the answer to `q`: the options that matched, or — when none did — the answer
 * text itself.
 *
 * "Other" is free text, so it is never one of the declared options and matching against them can
 * only come up empty. Showing a dash there loses something the user typed: the answer reached the
 * CLI, and re-reading the chat is the one place it can still be seen.
 */
function answerText(q: AskQuestion, answered: string): string {
    const chosen = chosenLabels(q, answered);
    if (chosen.length) {
        return chosen.join(', ');
    }
    return questionAnswer(q, answered) || '—';
}

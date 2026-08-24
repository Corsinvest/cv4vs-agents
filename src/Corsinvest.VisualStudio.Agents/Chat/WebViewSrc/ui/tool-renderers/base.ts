/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Base tool renderer. One subclass per tool, bound to a ToolHost (the seam to
// the app). Each tool overrides only what differs and returns its whole row
// from row() — the component never branches on tool name or behaviour flags.
//
// row(ctx):    the entire row (chrome + content). Pick a layout building block
//              (rowStandard / rowDiff / rowCount / rowHeaderOnly) or build your
//              own. Default: rowStandard (header + collapsible IN/OUT body).
// header():    the row label — name span + optional secondary detail.
// body():      the collapsible content. Default: the IN/OUT grid.
//
// Pure helpers (cleanResult/preview/diff summary/link markup/row layout) live
// here. Only actions that touch the app go through this.host.

import { html, nothing, type TemplateResult } from 'lit';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import ErrorCircle16Regular from '@fluentui/svg-icons/icons/error_circle_16_regular.svg';
import ChevronDown16Regular from '@fluentui/svg-icons/icons/chevron_down_16_regular.svg';
import VisualStudioIcon from '../icons/visualStudio.svg';
import { truncate, formatDurationSec } from '../helpers/format';
import '../components/cv-copy-btn';
import '../components/cv-diff-preview';
import { cleanResult, previewText } from './tool-host';
import { state as appState } from '../../core/state';
import { countChanges } from '../../core/diff-stats';
import { renderMarkdown } from '../../core/markdown';
import { highlightCode } from '../../core/lang';
import type { ToolHost } from './types';

export abstract class ToolRenderer {
    constructor(protected host: ToolHost) {}

    /** Tool name this renderer handles ('' for the catch-all default). */
    abstract readonly name: string;

    /** The whole row. Default: standard header + collapsible IN/OUT body. */
    row(): TemplateResult {
        return this.rowStandard();
    }

    /** Header label. Default: name span + a best-effort detail field. */
    header(): TemplateResult {
        return html`${this.nameSpan(this.label())}${this.detailSpan(this.detailText())}`;
    }

    /** Body content. Default: the IN/OUT grid. null = no body. */
    body(): TemplateResult | null {
        return this.ioGrid(this.inputText());
    }

    /** Name shown in the header. Default: the raw tool name. */
    label(): string {
        return this.host.name;
    }

    /** Best-effort header detail when a tool doesn't override header(). */
    detailText(): string {
        const i = this.host.input;
        const d = i.query ?? i.url ?? i.message ?? i.channel ?? i.title ?? i.repo ?? i.path ?? '';
        return truncate(String(d), 80);
    }

    /** Text for the IN cell of the default body. Default: pretty raw input. */
    inputText(): string {
        const input = this.host.input;
        if (Object.keys(input).length > 0) {
            try {
                return JSON.stringify(input, null, 2);
            } catch {
                return '';
            }
        }
        return '';
    }

    /** Header + collapsible IN/OUT body (the common case). */
    protected rowStandard(): TemplateResult {
        const body = this.body();
        // "Something to expand into": a body, or (Agent) live sub-agent children. Not gated on the
        // pending status, so the Agent chevron appears while the sub-agent runs.
        const expandable = this.hasExpandableContent();
        // Only defaultCollapsed rows (Agent) are collapsible: they start closed regardless of
        // the preview setting and show a chevron (kept visible at rest) to toggle. Every other
        // tool opens per autoOpen and shows no chevron — its body just stays open.
        const collapsed = this.defaultCollapsed();
        const open =
            expandable && (collapsed ? this.host.expanded : this.autoOpen() || this.host.expanded);
        return this.chrome({
            body,
            open,
            // No explicit onClick: chrome's rowClick falls back to toggleExpanded when there's a chevron.
            onClick: null,
            chevron: expandable && collapsed,
            chevronAlwaysShown: collapsed,
        });
    }

    /** Header only, no body (Read, ToolSearch, plan/worktree tools). */
    protected rowHeaderOnly(): TemplateResult {
        return this.chrome({ body: null, open: false, onClick: null, chevron: false });
    }

    /** Header + a single "N matches" count line instead of a body (Grep/Glob).
     *  The line is clickable (opens the full output in VS) when there are hits. */
    protected rowCount(unit: string, empty: string): TemplateResult {
        let line: TemplateResult | null = null;
        if (this.host.status === 'done') {
            // Full line count from the host: the result here is preview-clipped, so
            // counting it would under-report (e.g. show 50 instead of 101).
            const n = this.host.fullLineCount;
            const singular = unit.replace(/s$/, '');
            const label = n === 0 ? empty : n === 1 ? `1 ${singular}` : `${n} ${unit}`;
            line =
                n === 0
                    ? html`<span class="cv-tool-row-count">${label}</span>`
                    : html`<span
                          class="cv-tool-row-count cv-tool-row-count-clickable"
                          @click=${() => this.host.openOutput('out')}
                          >${label}</span
                      >`;
        }
        return this.chrome({ body: line, open: line !== null, onClick: null, chevron: false });
    }

    /** Diff tools (Edit/Write/MultiEdit): body shows even while pending, the row
     *  click opens the file at the edit, the header gets the VS/error buttons. */
    protected rowDiff(): TemplateResult {
        const fp = String(this.host.input.file_path ?? this.host.input.path ?? '');
        return this.chrome({
            body: this.diffBody(),
            open: true,
            onClick: () => this.host.openFileAtEdit(fp),
            chevron: false,
        });
    }

    /** Whether the row has something to expand into (drives the chevron + row toggle). Default: a
     *  non-empty body. Agent widens this to include its live sub-agent children, so the chevron
     *  shows while the sub-agent runs, not only once it finishes. */
    protected hasExpandableContent(): boolean {
        return this.body() !== null;
    }

    /** Whether a standard body opens without a click. Default: error rows, or
     *  when previews are on. */
    protected autoOpen(): boolean {
        return this.host.status === 'error' || appState.ui.previewLines > 0;
    }

    /** Whether this row starts collapsed, ignoring the preview auto-open setting.
     *  Default: false — a tool row follows autoOpen (error/previews). A row that holds
     *  a lot (Agent: a whole sub-agent transcript) overrides this to start closed and
     *  keep its chevron visible at rest, so it clearly reads as expandable. */
    protected defaultCollapsed(): boolean {
        return false;
    }

    /** The row itself. The rowX helpers above are the shared shapes; a tool whose body is its
     *  own (Ask, TodoWrite) calls this directly rather than through a shape that, not knowing
     *  what the body holds, can't tell whether there is anything to expand into. */
    protected chrome(opts: {
        body: TemplateResult | null;
        open: boolean;
        onClick: (() => void) | null;
        chevron: boolean;
        chevronAlwaysShown?: boolean;
    }): TemplateResult {
        // A custom onClick wins (e.g. Edit opens the file); otherwise a chevron makes the WHOLE row
        // the toggle target (accordion-style). The chevron button stopPropagation()s, so clicking it
        // and clicking the row can't double-fire.
        const rowClick = opts.onClick ?? (opts.chevron ? () => this.host.toggleExpanded() : null);
        const clickable = rowClick !== null;
        const elapsed = this.host.elapsedSec;
        const wrapCls = `cv-tool-wrap${clickable ? '' : ' no-row-click'}`;
        return html`
            <div class=${wrapCls}>
                <div
                    class="cv-tool-row"
                    style=${clickable ? 'cursor:pointer' : ''}
                    @click=${rowClick}
                >
                    <span class="cv-tool-row-dot ${this.dotClass()}"></span>
                    ${this.header()}
                    ${
                        elapsed > 0
                            ? html`<span class="cv-tool-row-progress"
                                  >${formatDurationSec(elapsed)}</span
                              >`
                            : nothing
                    }
                    ${this.renderHeaderActions()}
                    ${
                        opts.chevron
                            ? html`<fluent-button
                                  class="trigger cv-tool-row-chev ${opts.open ? 'expanded' : ''} ${
                                      opts.chevronAlwaysShown ? 'always-shown' : ''
                                  }"
                                  appearance="subtle"
                                  shape="rounded"
                                  size="small"
                                  icon-only
                                  title=${opts.open ? 'Collapse' : 'Expand'}
                                  @click=${(e: Event) => {
                                      e.stopPropagation();
                                      rowClick?.();
                                  }}
                              >
                                  ${unsafeHTML(ChevronDown16Regular)}
                              </fluent-button>`
                            : nothing
                    }
                </div>
                ${opts.open ? opts.body : nothing}
                ${opts.open ? this.host.renderChildren() : nothing}
            </div>
        `;
    }

    /** Whether the row should show as still running even if its own tool_result
     *  already arrived. Agent overrides this: its result is just launch metadata,
     *  the real work runs on in the sub-agent (tracked via subagentTasks). */
    protected isRunning(): boolean {
        return this.host.status === 'pending';
    }

    private dotClass(): string {
        if (this.isRunning()) {
            return 'spinning';
        }
        switch (this.host.status) {
            case 'done':
                return 'dot-done';
            case 'error':
                return 'dot-error';
            default:
                return 'spinning';
        }
    }

    protected nameSpan(text: string): TemplateResult {
        return html`<span class="cv-tool-row-name">${text}</span>`;
    }
    protected detailSpan(content: unknown): TemplateResult {
        return html`<span class="cv-tool-row-detail">${content}</span>`;
    }

    protected fileLink(
        filePath: string,
        label: unknown,
        startLine = 0,
        endLine = 0,
    ): TemplateResult {
        if (!filePath) {
            return html`<span>${label}</span>`;
        }
        return html`<a
            class="cv-tool-row-link"
            title=${filePath}
            @click=${(e: Event) => {
                e.stopPropagation();
                this.host.openFile(filePath, startLine, endLine);
            }}
            >${label}</a
        >`;
    }

    protected editFileLink(filePath: string, label: unknown): TemplateResult {
        if (!filePath) {
            return html`<span>${label}</span>`;
        }
        return html`<a
            class="cv-tool-row-link"
            title=${filePath}
            @click=${(e: Event) => {
                e.stopPropagation();
                this.host.openFileAtEdit(filePath);
            }}
            >${label}</a
        >`;
    }

    protected urlLink(url: string, label: unknown): TemplateResult {
        return html`<a
            class="cv-tool-row-link"
            title=${url}
            @click=${(e: Event) => {
                e.stopPropagation();
                this.host.openUrl(url);
            }}
            >${label}</a
        >`;
    }

    /** A path:line reference inside a markdown cell opens that file, not the cell's own temp doc —
     *  the enclosing row listens for clicks too, so this has to stop the event from reaching it. */
    protected onMarkdownClick = (e: Event): void => {
        const a = (e.target as HTMLElement | null)?.closest('a.cv-file-link');
        const file = a?.getAttribute('data-file');
        if (!file) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        const line = Number(a?.getAttribute('data-line') ?? 0) || 0;
        this.host.openFile(file, line, Number(a?.getAttribute('data-line-end') ?? 0) || line);
    };

    /** The standard IN/OUT body: raw input (IN) + cleaned output (OUT), each a clickable cell
     *  (opens in VS) with an inline copy button. `showOut: false` renders IN only; `inLabel: ''`
     *  drops the IN label; `markdown: true` is for cells holding prose instead of tool output. */
    protected ioGrid(
        inText = '',
        opts: {
            showOut?: boolean;
            inLabel?: string;
            markdown?: boolean;
            highlightAs?: string;
        } = {},
    ): TemplateResult {
        const { showOut = true, inLabel = 'IN', markdown = false, highlightAs = '' } = opts;
        const outText = showOut ? cleanResult(this.host.result, this.host.status === 'error') : '';
        if (!inText && !outText) {
            return html`${nothing}`;
        }
        // Markdown cells hold prose (an Agent's prompt and its report), so they render as rich
        // text and in full: clipping a paragraph mid-sentence hides the answer, and the preview
        // cap exists for tool output — logs, file dumps — where the first lines are enough.
        const cell = (t: string, extra = '') => {
            if (markdown) {
                return html`<div class="cv-tool-body-md md" @click=${this.onMarkdownClick}>
                    ${unsafeHTML(renderMarkdown(t))}
                </div>`;
            }
            const shown = previewText(
                t,
                appState.ui.previewLines,
                this.host.expanded,
                this.host.clipsOutput,
            );
            // Highlight only what a caller asked to (Write's file content): tool OUTPUT is logs
            // and dumps, where colouring guesses at structure that isn't there.
            const code = highlightAs ? highlightCode(shown, highlightAs) : null;
            return code
                ? html`<pre
                      class="cv-tool-body-pre hljs ${extra}"
                  ><code>${unsafeHTML(code)}</code></pre>`
                : html`<pre class="cv-tool-body-pre ${extra}">${shown}</pre>`;
        };
        const copyBtn = (text: string, slot: 'in' | 'out') =>
            html`<cv-copy-btn
                class="cv-tool-body-copy-btn cv-tool-body-copy-${slot}"
                .text=${text}
                title="Copy"
            ></cv-copy-btn>`;
        return html`
            <div class="cv-tool-body">
                <div class="cv-tool-body-box">
                    ${
                        inText
                            ? html`<div
                                  class="cv-tool-body-row cv-tool-body-row-in"
                                  style="cursor:pointer"
                                  @click=${() => this.host.openOutput('in')}
                              >
                                  ${
                                      inLabel
                                          ? html`<span class="cv-tool-body-label">${inLabel}</span>`
                                          : nothing
                                  }
                                  <div class="cv-tool-body-cell">
                                      ${cell(inText)}${copyBtn(inText, 'in')}
                                  </div>
                              </div>`
                            : nothing
                    }
                    ${
                        outText
                            ? html`<div
                                  class="cv-tool-body-row cv-tool-body-row-out"
                                  style="cursor:pointer"
                                  @click=${() => this.host.openOutput('out')}
                              >
                                  <span class="cv-tool-body-label"
                                      >${this.host.status === 'error' ? 'ERR' : 'OUT'}</span
                                  >
                                  <div class="cv-tool-body-cell">
                                      ${cell(outText, 'cv-tool-body-result')}${copyBtn(outText, 'out')}
                                  </div>
                              </div>`
                            : nothing
                    }
                </div>
            </div>
        `;
    }

    /** The inline diff body for Edit/Write/MultiEdit. */
    protected diffBody(): TemplateResult {
        const inp = this.host.input;
        const fp = String(inp.file_path ?? inp.path ?? '');
        const oldS = String(inp.old_string ?? '');
        const newS = String(inp.new_string ?? inp.content ?? '');
        const errBox =
            this.host.status === 'error' && this.host.result && appState.ui.showInlineToolErrors
                ? html`<div class="cv-tool-body-error">${cleanResult(this.host.result, true)}</div>`
                : nothing;
        return html`
            <div
                class="cv-tool-body"
                style="cursor:pointer"
                @click=${() => this.host.openDiffInVs()}
            >
                <cv-diff-preview
                    .oldString=${oldS}
                    .newString=${newS}
                    .filePath=${fp}
                    .patch=${this.host.diffPatch}
                ></cv-diff-preview>
                ${errBox}
            </div>
        `;
    }

    /** Change counts for the row header, trailing the path ("Edit failed" / "+3 −14" / "Modified"). */
    protected diffSummary(oldS: string, newS: string): TemplateResult {
        if (this.host.status === 'error') {
            return html`Edit failed`;
        }
        const fp = String(this.host.input.file_path ?? this.host.input.path ?? '');
        const { added, removed } = countChanges(oldS, newS, fp, appState.ui.diffIgnoreWhitespace);
        if (!added && !removed) {
            return html`Modified`;
        }
        return html`${added ? html`<span class="cv-diff-count-ins">+${added}</span>` : nothing}
        ${removed ? html`<span class="cv-diff-count-del">−${removed}</span>` : nothing}`;
    }

    /** The header actions this tool shows (right of the header, before the chevron). Default: an
     *  error button on a failed tool, nothing otherwise. Renderers override to add their own. */
    protected renderHeaderActions(): TemplateResult | typeof nothing {
        return this.host.status === 'error' ? this.errorButton() : nothing;
    }

    /** The "show error details in VS" icon button. */
    protected errorButton(): TemplateResult {
        return html`<div class="cv-tool-actions">
            <fluent-button
                class="trigger cv-tool-actions-error"
                appearance="subtle"
                shape="rounded"
                size="small"
                icon-only
                title="Show error details"
                @click=${(e: Event) => {
                    e.stopPropagation();
                    this.host.openError();
                }}
            >
                ${unsafeHTML(ErrorCircle16Regular)}
            </fluent-button>
        </div>`;
    }

    /** Diff tools' header buttons: "open diff in VS" + (on error) "show error". */
    protected diffActionButtons(): TemplateResult {
        return html`<div class="cv-tool-actions">
            ${
                this.host.status === 'error'
                    ? html`<fluent-button
                          class="trigger cv-tool-actions-error"
                          appearance="subtle"
                          shape="rounded"
                          size="small"
                          icon-only
                          title="Show error details"
                          @click=${(e: Event) => {
                              e.stopPropagation();
                              this.host.openError();
                          }}
                      >
                          ${unsafeHTML(ErrorCircle16Regular)}
                      </fluent-button>`
                    : nothing
            }
            <fluent-button
                class="trigger cv-tool-actions-vs"
                appearance="subtle"
                shape="rounded"
                size="small"
                icon-only
                title="Open diff in Visual Studio"
                @click=${(e: Event) => {
                    e.stopPropagation();
                    this.host.openDiffInVs();
                }}
            >
                ${unsafeHTML(VisualStudioIcon)}
            </fluent-button>
        </div>`;
    }
}

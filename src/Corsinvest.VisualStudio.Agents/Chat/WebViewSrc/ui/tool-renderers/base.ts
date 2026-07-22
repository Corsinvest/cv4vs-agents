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
import { truncate } from '../helpers/format';
import '../components/cv-copy-btn';
import '../components/cv-diff-preview';
import { cleanResult, previewText, formatElapsed } from './tool-host';
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
        const hasBody = body !== null && this.host.status !== 'pending';
        // Only defaultCollapsed rows (Agent) are collapsible: they start closed regardless of
        // the preview setting and show a chevron (kept visible at rest) to toggle. Every other
        // tool opens per autoOpen and shows no chevron — its body just stays open.
        const collapsed = this.defaultCollapsed();
        const open =
            hasBody && (collapsed ? this.host.expanded : this.autoOpen() || this.host.expanded);
        return this.chrome({
            body: hasBody ? body : null,
            open,
            onClick: hasBody && collapsed ? () => this.host.toggleExpanded() : null,
            chevron: hasBody && collapsed,
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
            actions: this.diffActionButtons(),
        });
    }

    /** Header + a custom body the tool builds (todos, questions). */
    protected rowCustom(body: TemplateResult, autoOpen = true): TemplateResult {
        const show = this.host.status !== 'pending';
        return this.chrome({
            body: show ? body : null,
            open: show && (autoOpen || this.host.expanded),
            onClick: show ? () => this.host.toggleExpanded() : null,
            chevron: show,
        });
    }

    /** Whether a standard body opens without a click. Default: error rows, or
     *  when previews are on. */
    protected autoOpen(): boolean {
        return this.host.status === 'error' || this.host.previewLines > 0;
    }

    /** Whether this row starts collapsed, ignoring the preview auto-open setting.
     *  Default: false — a tool row follows autoOpen (error/previews). A row that holds
     *  a lot (Agent: a whole sub-agent transcript) overrides this to start closed and
     *  keep its chevron visible at rest, so it clearly reads as expandable. */
    protected defaultCollapsed(): boolean {
        return false;
    }

    private chrome(opts: {
        body: TemplateResult | null;
        open: boolean;
        onClick: (() => void) | null;
        chevron: boolean;
        chevronAlwaysShown?: boolean;
        actions?: TemplateResult;
    }): TemplateResult {
        // When the chevron is present it is the toggle (a real button), so the row itself
        // is not clickable — otherwise the two double-trigger. Rows without a chevron keep
        // their row-level click (e.g. diff rows opening the file).
        const rowClick = opts.chevron ? null : opts.onClick;
        const clickable = rowClick !== null;
        const actions =
            opts.actions ?? (this.host.status === 'error' ? this.errorButton() : nothing);
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
                                  >${formatElapsed(elapsed)}</span
                              >`
                            : nothing
                    }
                    ${actions}
                    ${
                        opts.chevron && opts.body !== null
                            ? html`<fluent-button
                                  appearance="subtle"
                                  size="small"
                                  icon-only
                                  class="cv-tool-row-chev ${opts.open ? 'expanded' : ''} ${
                                      opts.chevronAlwaysShown ? 'always-shown' : ''
                                  }"
                                  title=${opts.open ? 'Collapse' : 'Expand'}
                                  @click=${(e: Event) => {
                                      e.stopPropagation();
                                      opts.onClick?.();
                                  }}
                                  >${unsafeHTML(ChevronDown16Regular)}</fluent-button
                              >`
                            : nothing
                    }
                </div>
                ${opts.open ? opts.body : nothing}
                ${opts.open ? this.host.renderSubagentChildren() : nothing}
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

    /** The standard IN/OUT body: raw input (IN) + cleaned output (OUT), each a
     *  clickable cell (opens in VS) with an inline copy button. Pass showOut=false
     *  to render IN only (e.g. Agent, whose result is just launch metadata). */
    protected ioGrid(inText = '', showOut = true): TemplateResult {
        const outText = showOut ? cleanResult(this.host.result, this.host.status === 'error') : '';
        if (!inText && !outText) {
            return html`${nothing}`;
        }
        const preview = (t: string) =>
            previewText(t, this.host.previewLines, this.host.expanded, this.host.clipsOutput);
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
                                  <span class="cv-tool-body-label">IN</span>
                                  <div class="cv-tool-body-cell">
                                      <pre class="cv-tool-body-pre">${preview(inText)}</pre>
                                      ${copyBtn(inText, 'in')}
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
                                  <span class="cv-tool-body-label cv-tool-body-label-out"
                                      >${this.host.status === 'error' ? 'ERR' : 'OUT'}</span
                                  >
                                  <div class="cv-tool-body-cell">
                                      <pre class="cv-tool-body-pre cv-tool-body-result">
${preview(outText)}</pre>
                                      ${copyBtn(outText, 'out')}
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
            this.host.status === 'error' && this.host.result && this.host.showInlineToolErrors
                ? html`<div class="cv-tool-body-error">${cleanResult(this.host.result, true)}</div>`
                : nothing;
        return html`
            <div
                class="cv-tool-body"
                style="cursor:pointer"
                @click=${() => this.host.openDiffDialog(fp, oldS, newS)}
            >
                <div class="cv-diff-summary ${this.host.status === 'error' ? 'is-error' : ''}">
                    ${this.diffSummary(oldS, newS)}
                </div>
                <cv-diff-preview
                    .oldString=${oldS}
                    .newString=${newS}
                    .filePath=${fp}
                ></cv-diff-preview>
                ${errBox}
            </div>
        `;
    }

    /** One-line summary above the diff ("Edit failed" / "Added N lines" / …). */
    private diffSummary(oldS: string, newS: string): string {
        if (this.host.status === 'error') {
            return 'Edit failed';
        }
        const oldLines = (oldS || '').split('\n').length;
        const newLines = (newS || '').split('\n').length;
        const added = Math.max(0, newLines - oldLines);
        const removed = Math.max(0, oldLines - newLines);
        const pl = (n: number) => (n !== 1 ? 's' : '');
        if (added > 0 && removed > 0) {
            return `Added ${added} line${pl(added)}, removed ${removed} line${pl(removed)}`;
        }
        if (added > 0) {
            return `Added ${added} line${pl(added)}`;
        }
        if (removed > 0) {
            return `Removed ${removed} line${pl(removed)}`;
        }
        return 'Modified';
    }

    /** The "show error details in VS" icon button. */
    protected errorButton(): TemplateResult {
        return html`<div class="cv-tool-actions">
            <fluent-button
                appearance="subtle"
                size="small"
                icon-only
                class="cv-tool-actions-error"
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
        const inp = this.host.input;
        const fp = String(inp.file_path ?? inp.path ?? '');
        const oldS = String(inp.old_string ?? '');
        const newS = String(inp.new_string ?? inp.content ?? '');
        return html`<div class="cv-tool-actions">
            ${
                this.host.status === 'error'
                    ? html`<fluent-button
                          appearance="subtle"
                          size="small"
                          icon-only
                          class="cv-tool-actions-error"
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
            ${
                this.host.showOpenDiffInVsButton
                    ? html`<fluent-button
                          appearance="subtle"
                          size="small"
                          icon-only
                          class="cv-tool-actions-vs"
                          title="Open diff in Visual Studio"
                          @click=${(e: Event) => {
                              e.stopPropagation();
                              this.host.openDiffInVs(fp, oldS, newS);
                          }}
                      >
                          ${unsafeHTML(VisualStudioIcon)}
                      </fluent-button>`
                    : nothing
            }
        </div>`;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { buildRows, rowsFromHunks, type Row, type Seg } from '../../core/diff-rows';
import type { PatchHunkDto } from '../../core/generated/PatchHunkDto';
import { highlightCode, langForFile } from '../../core/lang';
import { markRanges, rangesOf } from '../../core/mark-ranges';

/** Unchanged lines kept around each change — what git and GitHub show. */
const CONTEXT_LINES = 3;

/** Rows shown before the preview stops; the whole diff opens in Visual Studio. */
const VISIBLE_ROWS = 12;

/**
 * Inline diff preview for tool rows (Edit / Write / MultiEdit).
 *
 * Three layers: the patch says which rows changed, the word-diff which piece inside a row, the
 * highlighter what the code says. The highlighter needs the whole line — given a changed piece
 * alone it sees a word out of context and returns `boolean` plain, not as a keyword — so it runs
 * first and markRanges puts the marks over its HTML.
 */
@customElement('cv-diff-preview')
export class CvDiffPreview extends LitElement {
    @property() oldString = '';
    @property() newString = '';
    @property() filePath = '';

    /** The CLI's own hunks, once its result has arrived. Preferred over diffing the two input
     *  fragments: only these know the file's real line numbers and carry the surrounding context. */
    @property({ attribute: false }) patch: PatchHunkDto[] | null = null;

    // Light DOM: the row that renders this is itself Light DOM
    // (cv-tool-row.ts:77), and the styles live in the global diff.css. Moving
    // this component to a shadow root belongs to the CSS migration, not here.
    override createRenderRoot() {
        return this;
    }

    /** The row's text, highlighted whole and then marked. Null (unknown language, or hljs threw)
     *  renders the segments plain, which is what an unhighlighted file should look like. */
    private _text(segs: Seg[], lang: string): TemplateResult {
        const line = segs.map((s) => s.text).join('');
        const hl = highlightCode(line, lang);
        if (!hl) {
            return html`${segs.map((s) => (s.changed ? html`<mark>${s.text}</mark>` : s.text))}`;
        }
        return html`${unsafeHTML(markRanges(hl, rangesOf(segs)))}`;
    }

    private _row(row: Row, lang: string, numbered: boolean): TemplateResult {
        if (row.kind === 'hunk') {
            return html`<div class="cv-diff-hunk"></div>`;
        }
        const sign = row.kind === 'ins' ? '+' : row.kind === 'del' ? '-' : ' ';
        // One gutter: a '-' exists only in the old file and a '+' only in the new, so every
        // row has exactly one number worth showing.
        const no = row.kind === 'del' ? row.oldNo : row.newNo;
        return html`<div class="cv-diff-row cv-diff-${row.kind}">
            ${numbered ? html`<span class="cv-diff-ln">${no ?? ''}</span>` : nothing}
            <span class="cv-diff-sign">${sign}</span>
            <span class="cv-diff-txt">${this._text(row.segs, lang)}</span>
        </div>`;
    }

    override render() {
        if (!this.oldString && !this.newString) {
            return html`<div class="cv-diff-empty">No changes</div>`;
        }
        // Until the tool_result lands there is no patch, only the two fragments the input carried.
        // Diffing those is the best available answer, but their line numbers describe the fragment
        // and not the file — so that branch renders without a gutter rather than with a wrong one.
        const fromCli = !!this.patch?.length;
        const all = fromCli
            ? rowsFromHunks(this.patch)
            : buildRows(this.oldString, this.newString, this.filePath, CONTEXT_LINES);
        // The leading hunk marker separates nothing. Dropped before the slice below, or it would
        // still spend one of the visible rows.
        const rows = all[0]?.kind === 'hunk' ? all.slice(1) : all;
        if (!rows.length) {
            return html`<div class="cv-diff-empty">No changes</div>`;
        }
        // Only what fits is built: a row past the visible ones is DOM nobody
        // can reach, and the full diff is one click away in Visual Studio.
        const shown = rows.slice(0, VISIBLE_ROWS);
        const more = rows.length - shown.length;
        // langForFile, not the extension: an extensionless name is a language too — a Dockerfile
        // went unhighlighted here for as long as this read the extension itself.
        const lang = langForFile(this.filePath);
        return html`<div
            class="cv-diff-preview-wrap ${fromCli ? '' : 'no-gutter'}"
            data-action="diff-expand"
        >
            ${shown.map((r) => this._row(r, lang, fromCli))}
            ${more > 0 ? html`<div class="cv-diff-more">… ${more} more lines</div>` : nothing}
        </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-diff-preview': CvDiffPreview;
    }
}

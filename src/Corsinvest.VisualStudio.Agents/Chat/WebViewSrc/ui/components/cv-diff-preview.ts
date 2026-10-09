/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { diffRowsHtml } from '../../core/diff-html';
import { buildRows, rowsFromHunks } from '../../core/diff-rows';
import type { PatchHunkDto } from '../../core/generated/PatchHunkDto';
import { langForFile } from '../../core/lang';

/** Unchanged lines kept around each change, what git and GitHub show. */
const CONTEXT_LINES = 3;

/** Rows shown before the preview stops; the whole diff opens in Visual Studio. */
const VISIBLE_ROWS = 12;

/**
 * Inline diff preview for tool rows (Edit / Write / MultiEdit).
 *
 * Three layers: the patch says which rows changed, the word-diff which piece inside a row, the
 * highlighter what the code says. diff-html.ts turns the three into the rows' markup.
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

    override render() {
        if (!this.oldString && !this.newString) {
            return html`<div class="cv-diff-empty">No changes</div>`;
        }
        // Until the tool_result lands there is no patch, only the two fragments the input carried.
        // Diffing those is the best available answer, but their line numbers describe the fragment
        // and not the file, so that branch renders without a gutter rather than with a wrong one.
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
        // langForFile, not the extension: an extensionless name is a language too: a Dockerfile
        // went unhighlighted here for as long as this read the extension itself.
        const lang = langForFile(this.filePath);
        return html`<div
            class="cv-diff-preview-wrap ${fromCli ? '' : 'no-gutter'}"
            data-action="diff-expand"
        >
            ${unsafeHTML(diffRowsHtml(shown, lang, fromCli))}
            ${more > 0 ? html`<div class="cv-diff-more">… ${more} more lines</div>` : nothing}
        </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-diff-preview': CvDiffPreview;
    }
}

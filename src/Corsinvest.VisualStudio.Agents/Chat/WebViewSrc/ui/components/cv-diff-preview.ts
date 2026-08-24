/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { buildRows, rowsFromHunks, type Row, type Seg } from '../../core/diff-rows';
import type { PatchHunkDto } from '../../core/generated/PatchHunkDto';
import { highlightCode } from '../../core/lang';
import { state as appState } from '../../core/state';

/** Unchanged lines kept around each change — what git and GitHub show. */
const CONTEXT_LINES = 3;

/** Rows shown before the preview stops; the whole diff opens in Visual Studio. */
const VISIBLE_ROWS = 12;

/**
 * Inline diff preview for tool rows (Edit / Write / MultiEdit).
 *
 * Three layers that do not fight each other, applied in this order: the patch
 * says which rows changed, the word-diff which piece inside a row, the
 * highlighter what the code says. The order is a constraint — the highlighter
 * returns HTML, and feeding that to the word-diff would cut its tags in half.
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

    /** Highlight one segment. Null (unknown language, or hljs threw) renders
     *  plain text, which is what an unhighlighted file should look like. */
    private _seg(seg: Seg, lang: string): TemplateResult {
        const hl = highlightCode(seg.text, lang);
        const inner = hl ? unsafeHTML(hl) : seg.text;
        return seg.changed ? html`<mark>${inner}</mark>` : html`${inner}`;
    }

    private _row(row: Row, lang: string, numbered: boolean): TemplateResult {
        if (row.kind === 'hunk') {
            return html`<div class="cv-diff-hunk">@@ ${row.oldNo} ${row.newNo} @@</div>`;
        }
        const sign = row.kind === 'ins' ? '+' : row.kind === 'del' ? '-' : ' ';
        // One gutter, not two: a '-' is only in the old file and a '+' only in the new, so each
        // row has exactly one number worth showing. Two columns spent a second 2.2em repeating
        // the context rows and blanking on every change.
        const no = row.kind === 'del' ? row.oldNo : row.newNo;
        return html`<div class="cv-diff-row cv-diff-${row.kind}">
            ${numbered ? html`<span class="cv-diff-ln">${no ?? ''}</span>` : nothing}
            <span class="cv-diff-sign">${sign}</span>
            <span class="cv-diff-txt">${row.segs.map((s) => this._seg(s, lang))}</span>
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
        const rows = fromCli
            ? rowsFromHunks(this.patch)
            : buildRows(
                  this.oldString,
                  this.newString,
                  this.filePath,
                  CONTEXT_LINES,
                  appState.ui.diffIgnoreWhitespace,
              );
        if (!rows.length) {
            return html`<div class="cv-diff-empty">No changes</div>`;
        }
        // Only what fits is built: a row past the visible ones is DOM nobody
        // can reach, and the full diff is one click away in Visual Studio.
        const shown = rows.slice(0, VISIBLE_ROWS);
        const more = rows.length - shown.length;
        // Extension only, like Write's renderer (renderers.ts): highlightCode resolves a fence
        // label or extension, not a full path, so the path itself would never match.
        const name = this.filePath.split(/[\\/]/).pop() ?? '';
        const dot = name.lastIndexOf('.');
        const lang = dot > 0 ? name.slice(dot + 1) : '';
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

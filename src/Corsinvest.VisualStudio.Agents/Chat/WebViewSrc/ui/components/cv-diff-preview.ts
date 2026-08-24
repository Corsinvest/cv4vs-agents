/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing, type TemplateResult } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { buildRows, type Row, type Seg } from '../../core/diff-rows';
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

    // Light DOM: the row that renders this is itself Light DOM
    // (cv-tool-row.ts:77), and the styles live in the global diff.css. Moving
    // this component to a shadow root belongs to the CSS migration, not here —
    // what diff2html forced was the *markup*, not the render root.
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

    private _row(row: Row, lang: string): TemplateResult {
        if (row.kind === 'hunk') {
            return html`<div class="cv-diff-hunk">@@ ${row.oldNo} ${row.newNo} @@</div>`;
        }
        const sign = row.kind === 'ins' ? '+' : row.kind === 'del' ? '-' : ' ';
        return html`<div class="cv-diff-row cv-diff-${row.kind}">
            <span class="cv-diff-ln">${row.oldNo ?? ''}</span>
            <span class="cv-diff-ln">${row.newNo ?? ''}</span>
            <span class="cv-diff-sign">${sign}</span>
            <span class="cv-diff-txt">${row.segs.map((s) => this._seg(s, lang))}</span>
        </div>`;
    }

    override render() {
        if (!this.oldString && !this.newString) {
            return html`<div class="cv-diff-empty">No changes</div>`;
        }
        const rows = buildRows(
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
        return html`<div class="cv-diff-preview-wrap" data-action="diff-expand">
            ${shown.map((r) => this._row(r, lang))}
            ${more > 0 ? html`<div class="cv-diff-more">… ${more} more lines</div>` : nothing}
        </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-diff-preview': CvDiffPreview;
    }
}

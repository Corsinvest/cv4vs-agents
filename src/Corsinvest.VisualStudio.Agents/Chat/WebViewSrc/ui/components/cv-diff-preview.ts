/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { buildPatch } from '../../core/diff';
import { clampPatch } from '../../core/patch-clamp';
import { state as appState } from '../../core/state';
import { renderDiff, SPLIT_THRESHOLD, type DiffFormat } from '../diff';
import { observeSize } from '../resize';

/** Unchanged lines kept around each change — what git and GitHub show. */
const CONTEXT_LINES = 3;

/** Rows the preview is tall enough to show. Measured: diff2html's compact row is ~20.6px. */
const VISIBLE_ROWS = 12;

/**
 * Patch lines built at all — tied to what fits, because the box does not scroll vertically
 * (see `_draw`: overflow-y is hidden, only the horizontal one is left to diff2html). A line
 * past the visible ones is DOM nobody can reach; the full diff lives in the expand dialog.
 *
 * Not a context setting either: context is per hunk, so a file with scattered edits produces
 * many hunks and a long patch whatever the context.
 */
const MAX_PATCH_LINES = VISIBLE_ROWS;

/**
 * Inline diff preview for tool rows (Edit / Write / MultiEdit). Lit emits only
 * the wrapper; diff2html paints inside it imperatively after `updated()` since
 * it mutates the DOM directly. A ResizeObserver swaps line-by-line ↔
 * side-by-side at the width threshold.
 */
@customElement('cv-diff-preview')
export class CvDiffPreview extends LitElement {
    @property() oldString = '';
    @property() newString = '';
    @property() filePath = '';

    private _wrap?: HTMLDivElement;
    private _patch = '';
    private _format: DiffFormat | null = null;
    private _unobserve?: () => void;

    override createRenderRoot() {
        return this;
    }

    override render() {
        if (!this.oldString && !this.newString) {
            return html`<div class="cv-diff-preview-wrap">
                <div class="cv-diff-empty">No changes</div>
            </div>`;
        }
        return html`<div class="cv-diff-preview-wrap" data-action="diff-expand"></div>`;
    }

    override firstUpdated(): void {
        this._wrap =
            this.querySelector<HTMLDivElement>('.cv-diff-preview-wrap[data-action]') ?? undefined;
        this._draw();
        // Observe the host, not the inner wrap: side-by-side content pins the
        // wrap above the threshold and blocks swap-back; the host follows the
        // message column width unaffected.
        this._unobserve = observeSize(this, () => this._maybeSwapFormat());
    }

    override updated(changed: Map<string, unknown>): void {
        if (changed.has('oldString') || changed.has('newString') || changed.has('filePath')) {
            this._wrap =
                this.querySelector<HTMLDivElement>('.cv-diff-preview-wrap[data-action]') ??
                undefined;
            this._format = null;
            this._draw();
        }
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._unobserve?.();
        this._unobserve = undefined;
    }

    private _draw(): void {
        const wrap = this._wrap;
        if (!wrap) {
            return;
        }
        // Clamp the patch, not just the height: the cap below hides rows diff2html has already
        // built, so a large edit would sit in the DOM in full to show a dozen lines.
        this._patch = clampPatch(
            buildPatch(
                this.oldString,
                this.newString,
                this.filePath,
                CONTEXT_LINES,
                appState.ui.diffIgnoreWhitespace,
            ),
            MAX_PATCH_LINES,
        );
        const fmt: DiffFormat =
            this.offsetWidth >= SPLIT_THRESHOLD ? 'side-by-side' : 'line-by-line';
        this._format = fmt;
        // Horizontal scroll is owned by diff2html per-pane (.d2h-file-side-diff /
        // .d2h-file-diff); a wrap-level x-scroll would merge panes and break scroll-syncing.
        wrap.style.maxHeight = `${VISIBLE_ROWS * 20 + 8}px`;
        wrap.style.overflowY = 'hidden';
        wrap.style.overflowX = 'hidden';
        wrap.innerHTML = '';
        renderDiff(wrap, this._patch, fmt);
    }

    private _maybeSwapFormat(): void {
        const wrap = this._wrap;
        if (!wrap || !this._patch) {
            return;
        }
        const wantSplit = this.offsetWidth >= SPLIT_THRESHOLD;
        const isSplit = this._format === 'side-by-side';
        if (wantSplit === isSplit) {
            return;
        }
        const fmt: DiffFormat = wantSplit ? 'side-by-side' : 'line-by-line';
        this._format = fmt;
        wrap.innerHTML = '';
        renderDiff(wrap, this._patch, fmt);
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-diff-preview': CvDiffPreview;
    }
}

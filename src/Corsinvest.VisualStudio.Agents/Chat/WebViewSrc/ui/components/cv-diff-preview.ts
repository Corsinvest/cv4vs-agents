/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html } from 'lit';
import { customElement, property } from 'lit/decorators.js';
import { buildPatch } from '../../core/diff';
import { state as appState } from '../../core/state';
import { renderDiff, SPLIT_THRESHOLD, type DiffFormat } from '../diff';
import { observeSize } from '../resize';

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
        const previewLines = appState.ui.diffContextLines;
        this._patch = buildPatch(
            this.oldString,
            this.newString,
            this.filePath,
            previewLines,
            appState.ui.diffIgnoreWhitespace,
        );
        const fmt: DiffFormat =
            this.offsetWidth >= SPLIT_THRESHOLD ? 'side-by-side' : 'line-by-line';
        this._format = fmt;
        // Height cap ~20px/row clips to the first N lines. Horizontal scroll is
        // owned by diff2html per-pane (.d2h-file-side-diff / .d2h-file-diff);
        // a wrap-level x-scroll would merge panes and break scroll-syncing.
        wrap.style.maxHeight = `${previewLines * 20 + 8}px`;
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

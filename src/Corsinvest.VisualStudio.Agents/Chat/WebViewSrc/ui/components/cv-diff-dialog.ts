/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, nothing } from 'lit';
import { customElement, property, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import Wand16Regular from '@fluentui/svg-icons/icons/wand_16_regular.svg';
import LayoutColumnTwo16Regular from '@fluentui/svg-icons/icons/layout_column_two_16_regular.svg';
import List16Regular from '@fluentui/svg-icons/icons/list_16_regular.svg';
import Code16Regular from '@fluentui/svg-icons/icons/code_16_regular.svg';
import hljs from 'highlight.js';
import { buildPatch } from '../../core/diff';
import { state as appState } from '../../core/state';
import { fileName } from '../../core/path';
import { displayPathUi } from '../paths';
import { iconUrl } from '../../core/icon-url';
import { escapeHtml } from '../../core/html';
import { renderDiff, SPLIT_THRESHOLD, type DiffFormat } from '../diff';
import { observeSize } from '../resize';
import type { DiffDialogNotification } from '../../core/types';
import { CvDialogBase } from './cv-dialog-base';
import './cv-copy-btn';
import './cv-segmented';
import type { SegOption } from './cv-segmented';

type Mode = 'auto' | 'split' | 'unified' | 'patch';

const MODE_ICONS: Record<Mode, string> = {
    auto: Wand16Regular,
    split: LayoutColumnTwo16Regular,
    unified: List16Regular,
    patch: Code16Regular,
};

// Segmented options for the diff-mode picker (icon-only; the label is the tooltip).
const MODES: ReadonlyArray<SegOption<Mode>> = (['auto', 'split', 'unified', 'patch'] as Mode[]).map(
    (m) => ({ value: m, label: m[0].toUpperCase() + m.slice(1), icon: MODE_ICONS[m] }),
);

/**
 * Full-screen diff viewer. Created on demand by dialog-host (`openDiffDialog()`),
 * which owns focus + the Esc stack; this component renders and emits `close`.
 * Only Fluent override is dialog width/height to span the viewport.
 * Modes: auto / split / unified / patch; auto swaps split ↔ unified by width.
 */
@customElement('cv-diff-dialog')
export class CvDiffDialog extends CvDialogBase {
    @property({ attribute: false }) req: DiffDialogNotification | null = null;
    @state() private _mode: Mode = 'auto';

    @query('#diff-dialog-body') private _body!: HTMLDivElement;

    private _patch = '';
    private _lastFormat: DiffFormat | 'patch' | null = null;
    private _unobserve?: () => void;

    // Light DOM (unlike the other dialogs' Shadow DOM): diff2html markup needs the global CSS.
    override createRenderRoot(): HTMLElement | this {
        return this;
    }

    override updated(changed: Map<string, unknown>): void {
        super.updated(changed);
        // Build + draw the diff once the body element is in the DOM (create-on-open renders it after
        // the show()). Not tied to the open-change: it fires when _body becomes available.
        if (this.open && this._body && !this._patch) {
            this._patch = buildPatch(
                this.req?.oldString,
                this.req?.newString,
                this.req?.filePath,
                Number.MAX_SAFE_INTEGER,
                appState.ui.diffIgnoreWhitespace,
            );
            this._lastFormat = null;
            this._draw();
            this._unobserve?.();
            this._unobserve = observeSize(this._body, () => {
                if (this._mode === 'auto') {
                    this._draw();
                }
            });
        }
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._unobserve?.();
        this._unobserve = undefined;
    }

    private _draw(): void {
        const body = this._body;
        if (!body) {
            return;
        }
        if (!this._patch || this._patch.split('\n').length <= 4) {
            body.innerHTML = '<div class="cv-diff-empty">No changes</div>';
            this._lastFormat = null;
            return;
        }
        if (this._mode === 'patch') {
            const lang = hljs.getLanguage('diff');
            const highlighted = lang
                ? hljs.highlight(this._patch, { language: 'diff', ignoreIllegals: true }).value
                : escapeHtml(this._patch);
            // <cv-copy-btn frompre> reads the raw patch from the sibling <pre> on click.
            body.innerHTML =
                `<div class="cv-diff-patch-wrap">` +
                `<pre class="cv-diff-patch-text hljs"><code>${highlighted}</code></pre>` +
                `<cv-copy-btn class="cv-diff-patch-copy" frompre="1" title="Copy patch"></cv-copy-btn>` +
                `</div>`;
            this._lastFormat = 'patch';
            return;
        }
        const fmt: DiffFormat =
            this._mode === 'split' || (this._mode === 'auto' && body.offsetWidth >= SPLIT_THRESHOLD)
                ? 'side-by-side'
                : 'line-by-line';
        if (fmt === this._lastFormat) {
            return;
        }
        this._lastFormat = fmt;
        body.innerHTML = '';
        renderDiff(body, this._patch, fmt);
    }

    private _onModeChange = (e: CustomEvent<{ value: Mode }>) => {
        const v = e.detail.value;
        if (v === this._mode) {
            return;
        }
        this._mode = v;
        this._lastFormat = null;
        this._draw();
    };

    override render() {
        if (!this.open || !this.req) {
            return nothing;
        }
        // Title shows bare file name; the path (native separators) goes in the tooltip.
        const fullPath = displayPathUi(this.req.filePath) || this.req.filePath || 'Diff';
        const name = fileName(this.req.filePath) || 'Diff';
        return html`
            <fluent-dialog type="modal" aria-label="Diff viewer" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <span slot="title" class="cv-diff-dialog-title" title=${fullPath}>
                        <img
                            class="cv-diff-dialog-icon"
                            src=${iconUrl(name)}
                            width="16"
                            height="16"
                            alt=""
                        />
                        <span class="cv-diff-dialog-name">${name}</span>
                    </span>

                    <cv-segmented
                        slot="title-action"
                        .options=${MODES}
                        .activeValue=${this._mode}
                        @change=${this._onModeChange}
                    ></cv-segmented>

                    <fluent-button
                        slot="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Close"
                    >
                        ${unsafeHTML(Dismiss16Regular)}
                    </fluent-button>

                    <div id="diff-dialog-body"></div>
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-diff-dialog': CvDiffDialog;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import DocumentEdit16Regular from '@fluentui/svg-icons/icons/document_edit_16_regular.svg';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { iconUrl } from '../../core/icon-url';
import {
    changedFiles,
    countsNote,
    splitShownPath,
    stateLetter,
    undoBlocker,
} from '../../core/changed-files-store';
import {
    ChangedFilesReq,
    KeepAllChangedFilesReq,
    KeepChangedFileReq,
    UndoAllChangedFilesReq,
    UndoChangedFileReq,
} from '../../core/request-types';
import type {
    ChangedFileDto,
    ChangedFileFailureDto,
    ChangedFilesActionResult,
    ChangedFilesNotification,
    OpenChangedFileNotification,
} from '../../core/types';
import { displayPathUi } from '../paths';

// Keep all / Undo all close and restore every file one after the other, which on a long list is
// more than the default wait for a reply.
const ALL_TIMEOUT_MS = 120_000;

const INCOMPLETE = 'A command changed more files than could be followed, so others may be missing.';

/**
 * The files Claude changed in this session, behind a chip in the input row: a count, and a panel
 * with one row per file (state, +/- lines, Open / Keep / Undo) and Keep all / Undo all above them.
 * Hidden while the list is empty.
 *
 * Light DOM, unlike the other chips: the row buttons are the document's `.cv-icon-btn` and their
 * icons its `.cv-icon-*` masks (base.css), which a shadow root would cut off, and with up to two
 * hundred rows the component-per-button route is the expensive one (see CLAUDE.md). The rules
 * that are this component's own are in styles/changed-files.css.
 *
 * The host owns the list and pushes it whenever it changes; this only shows it and sends the
 * clicks back. An action's effect arrives the same way, as the next push.
 */
@customElement('cv-changed-files-chip')
export class CvChangedFilesChip extends LitElement {
    override createRenderRoot(): HTMLElement {
        return this;
    }

    @state() private _open = false;
    @state() private _confirmUndoAll = false;
    @state() private _busy = false;
    @state() private _problems: ChangedFileFailureDto[] = [];

    private _stops: Array<() => void> = [];

    override connectedCallback(): void {
        super.connectedCallback();
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
        this._stops.push(
            changedFiles.subscribe(() => this.requestUpdate()),
            bridge.onNotification<ChangedFilesNotification>(
                Msg.toWebView.changedFiles.updated,
                (list) => changedFiles.apply(list),
            ),
        );
        // A page that loads after the host already has a list would otherwise wait for its next change.
        void this._load();
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
        for (const stop of this._stops) {
            stop();
        }
        this._stops = [];
    }

    // Light dismiss: a click outside the chip closes the panel.
    private _onDocPointerDown = (e: PointerEvent): void => {
        if (this._open && !e.composedPath().includes(this)) {
            this._open = false;
            this._confirmUndoAll = false;
        }
    };

    private async _load(): Promise<void> {
        try {
            changedFiles.apply(await bridge.sendRequest(ChangedFilesReq, {}));
        } catch {
            // The session changed or the host is busy; it pushes the list again on its next change.
        }
    }

    override willUpdate(): void {
        // With no rows the chip is gone, and a panel left open would be there again, open, on the next one.
        if (this._open && changedFiles.files.length === 0 && !changedFiles.disabled) {
            this._open = false;
            this._confirmUndoAll = false;
        }
    }

    private _toggle = (): void => {
        this._open = !this._open;
        this._confirmUndoAll = false;
        if (this._open) {
            // Opening is when the numbers matter: the host measures the files against the disk again.
            void this._load();
        }
    };

    private _show(file: ChangedFileDto): void {
        bridge.sendNotification<OpenChangedFileNotification>(Msg.fromWebView.changedFiles.open, {
            path: file.path,
        });
    }

    private async _run(call: () => Promise<ChangedFilesActionResult>): Promise<void> {
        this._busy = true;
        this._problems = [];
        try {
            this._problems = (await call()).failed;
        } catch {
            this._problems = [{ path: '', reason: 'Visual Studio did not answer.' }];
        } finally {
            this._busy = false;
        }
    }

    private _keep(file: ChangedFileDto): void {
        void this._run(() => bridge.sendRequest(KeepChangedFileReq, { path: file.path }));
    }

    private _undo(file: ChangedFileDto): void {
        void this._run(() => bridge.sendRequest(UndoChangedFileReq, { path: file.path }));
    }

    private _keepAll = (): void => {
        void this._run(() => bridge.sendRequest(KeepAllChangedFilesReq, {}, ALL_TIMEOUT_MS));
    };

    private _undoAll = (): void => {
        this._confirmUndoAll = false;
        void this._run(() => bridge.sendRequest(UndoAllChangedFilesReq, {}, ALL_TIMEOUT_MS));
    };

    private _row(file: ChangedFileDto) {
        const { name, dir } = splitShownPath(displayPathUi(file.path));
        const blocker = undoBlocker(file);
        const note = countsNote(file);
        // Both can hold at once (a shell command's file that also has no copy): say both.
        const warning =
            [blocker, file.incomplete ? INCOMPLETE : null].filter(Boolean).join(' ') || null;
        return html`<div class="cv-changes-row" role="listitem">
            <button class="cv-changes-file" data-tip=${file.path} @click=${() => this._show(file)}>
                <img class="cv-changes-fileicon" src=${iconUrl(file.path)} alt="" />
                <span class="cv-changes-name">${name}</span>
                <span class="cv-changes-dir">${dir}</span>
            </button>
            <!-- A slot of its own, empty or not, so the columns after it line up from row to row. -->
            <span class="cv-changes-slot">
                ${
                    warning
                        ? html`<span
                              class="cv-icon cv-icon-warning cv-changes-warn"
                              data-tip=${warning}
                          ></span>`
                        : nothing
                }
            </span>
            <span class="cv-changes-state ${file.state}" data-tip=${file.state}
                >${stateLetter(file.state)}</span
            >
            <span
                class="cv-changes-counts"
                data-tip=${file.counts === 'approximate' ? 'Approximate: the files differ too much to count exactly.' : ''}
            >
                ${
                    note
                        ? html`<span class="cv-changes-quiet">${note}</span>`
                        : html`${file.counts === 'approximate' ? html`<span class="cv-changes-quiet">≈</span> ` : nothing}<span
                                  class="cv-diff-count-ins"
                                  >+${file.added}</span
                              >
                              <span class="cv-diff-count-del">−${file.deleted}</span>`
                }
            </span>
            <button
                class="cv-icon-btn"
                data-tip="Show what changed"
                aria-label=${`Show what changed in ${name}`}
                ?disabled=${this._busy}
                @click=${() => this._show(file)}
            >
                <span class="cv-icon cv-icon-open"></span>
            </button>
            <button
                class="cv-icon-btn"
                data-tip="Keep: accept the file as it is"
                aria-label=${`Keep ${name}`}
                ?disabled=${this._busy}
                @click=${() => this._keep(file)}
            >
                <span class="cv-icon cv-icon-checkmark"></span>
            </button>
            <button
                class="cv-icon-btn"
                data-tip=${blocker ?? 'Undo: put the file back as it was before Claude'}
                aria-label=${`Undo ${name}`}
                ?disabled=${this._busy || blocker !== null}
                @click=${() => this._undo(file)}
            >
                <span class="cv-icon cv-icon-arrow-undo"></span>
            </button>
        </div>`;
    }

    private _head(count: number) {
        return html`<div class="cv-changes-head">
            <span>Changed by Claude (${count})</span>
            ${
                this._confirmUndoAll
                    ? html`<span class="cv-changes-bulk">
                          Undo all ${count}?
                          <fluent-button
                              appearance="primary"
                              size="small"
                              ?disabled=${this._busy}
                              @click=${this._undoAll}
                              >Undo all</fluent-button
                          >
                          <fluent-button
                              appearance="subtle"
                              size="small"
                              @click=${() => (this._confirmUndoAll = false)}
                              >Cancel</fluent-button
                          >
                      </span>`
                    : html`<span class="cv-changes-bulk">
                          <fluent-button
                              appearance="subtle"
                              size="small"
                              ?disabled=${this._busy}
                              @click=${this._keepAll}
                              >Keep all</fluent-button
                          >
                          <fluent-button
                              appearance="subtle"
                              size="small"
                              ?disabled=${this._busy}
                              @click=${() => (this._confirmUndoAll = true)}
                              >Undo all</fluent-button
                          >
                      </span>`
            }
        </div>`;
    }

    private _panel(files: readonly ChangedFileDto[], disabled: string | null) {
        if (disabled) {
            return html`<div class="cv-changes-note">${disabled}</div>`;
        }
        return html`${this._head(files.length)}
            ${
                this._problems.length > 0
                    ? html`<div class="cv-changes-problems" role="alert">
                          ${this._problems.map(
                              (p) =>
                                  html`<div>
                                      ${p.path ? `${splitShownPath(displayPathUi(p.path)).name}: ` : ''}${p.reason}
                                  </div>`,
                          )}
                      </div>`
                    : nothing
            }
            <div class="cv-changes-list" role="list">${files.map((f) => this._row(f))}</div>
            ${
                changedFiles.overflow
                    ? html`<div class="cv-changes-note">
                          More files changed than are listed: the first ${changedFiles.maxFiles} are
                          shown.
                      </div>`
                    : nothing
            }
            <div class="cv-changes-note">Edits made by running a command are not listed.</div>`;
    }

    override render() {
        const files = changedFiles.files;
        const disabled = changedFiles.disabled;
        if (files.length === 0 && !disabled) {
            return nothing;
        }
        const n = files.length;
        const tip = disabled ?? `${n} file${n === 1 ? '' : 's'} changed by Claude`;
        return html`<fluent-button
                class="cv-changes-chip ${disabled ? 'off' : ''}"
                appearance="subtle"
                size="small"
                icon-only
                aria-label=${tip}
                data-tip=${tip}
                data-tip-place="after"
                @click=${this._toggle}
            >
                <span class="cv-changes-anchor">
                    ${unsafeHTML(DocumentEdit16Regular)}
                    ${
                        disabled
                            ? nothing
                            : html`<fluent-counter-badge
                                  class="cv-changes-count"
                                  count=${n}
                                  size="small"
                              ></fluent-counter-badge>`
                    }
                </span>
            </fluent-button>
            <div class="cv-changes-popover" ?hidden=${!this._open}>
                ${this._open ? this._panel(files, disabled) : nothing}
            </div>`;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-changed-files-chip': CvChangedFilesChip;
    }
}

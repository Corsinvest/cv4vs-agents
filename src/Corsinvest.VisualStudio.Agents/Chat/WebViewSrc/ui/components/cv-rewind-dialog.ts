/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { CvDialogBase } from './cv-dialog-base';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { RewindReq, RewindPointsReq } from '../../core/request-types';
import type {
    RewindPoint,
    RewindResultNotification,
    RewindDiffNotification,
} from '../../core/types';
import { displayPathUi } from '../paths';
import { iconStyles } from '../styles/shared';
import './cv-time-ago';

/**
 * "Rewind to…" — restore the files to the state the CLI captured before a chosen message.
 *
 * One dialog, two panes: the session's user messages above, and below them what rewinding to the
 * selected one would actually change. The impact comes from a `dry_run`, which reports the files
 * and the line counts without writing anything, so the answer to "what happens if I press this"
 * arrives before pressing it rather than after.
 *
 * Selecting a message never touches disk — only the Rewind button does. That separation is the
 * point: this list is meant to be browsed.
 */
@customElement('cv-rewind-dialog')
export class CvRewindDialog extends CvDialogBase {
    static override styles = [
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* A ceiling, not a height: with three messages a fixed 440px left the list stretched
               over a band of empty background. The dialog now grows with the list and stops before
               it runs off screen, at which point the list scrolls inside. */
            fluent-dialog-body::part(content) {
                max-height: min(440px, 70vh);
                display: flex;
                flex-direction: column;
                overflow: hidden;
            }
            .hint {
                color: var(--colorNeutralForeground2);
                font-size: var(--fontSizeBase200);
                margin: 0 0 8px;
            }
            /* Filter + list as one block: it is what the arrow keys act on, whichever of the two
               has focus. */
            .picker {
                display: flex;
                flex-direction: column;
                gap: 6px;
                min-height: 0;
            }
            /* Full width, past Fluent's own max-width — the same override cv-popover-list needs. */
            .search {
                width: 100%;
                max-width: none;
            }
            /* fluent-listbox brings the rows' look, their hover/selected states and the roles. Only
               the box it sits in is ours: it is built as a dropdown's popup, so on its own it comes
               with a shadow and a width of its content — inside a dialog it has to be a plain block
               that fills the width and scrolls. Layout only, no colours: the fill and the border
               stay the component's. */
            .list {
                display: flex;
                flex: 0 1 auto;
                width: 100%;
                min-height: 0;
                max-height: 100%;
                overflow-y: auto;
                box-sizing: border-box;
            }
            /* The two texts share the option's content cell and are spread apart inside it.
               ::part(content) is a handle the component offers, and display:flex on it is layout —
               unlike rewriting its grid areas, which is what putting the time in a slot of its own
               would have required. */
            .row::part(content) {
                display: flex;
                align-items: baseline;
                gap: 10px;
                min-width: 0;
            }
            .row-text {
                flex: 1 1 auto;
                min-width: 0;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
            }
            .row-when {
                flex: none;
                color: var(--colorNeutralForeground3);
                font-size: var(--fontSizeBase200);
            }
            /* The impact pane. Its own ground, so the two halves read as question and answer. */
            .impact {
                flex: 0 0 auto;
                max-height: 160px;
                overflow-y: auto;
                margin-top: 10px;
                padding: 8px;
                border-radius: var(--borderRadiusMedium);
                background: var(--colorNeutralBackground3);
                font-size: var(--fontSizeBase200);
            }
            .summary {
                color: var(--colorNeutralForeground2);
                margin-bottom: 6px;
            }
            .ins {
                color: var(--colorPaletteGreenForeground1);
            }
            .del {
                color: var(--colorPaletteRedForeground1);
            }
            .files {
                display: flex;
                flex-direction: column;
                gap: 1px;
            }
            /* A file opens VS's diff viewer — a link, because it navigates somewhere. */
            .file {
                background: none;
                border: none;
                padding: 2px 4px;
                border-radius: var(--borderRadiusSmall);
                font: inherit;
                font-family: var(--fontFamilyMonospace);
                color: var(--colorBrandForegroundLink);
                text-align: left;
                cursor: pointer;
                overflow: hidden;
                text-overflow: ellipsis;
                white-space: nowrap;
                outline: none;
            }
            .file:hover,
            .file:focus-visible {
                background: var(--colorSubtleBackgroundHover);
                text-decoration: underline;
            }
            .muted {
                color: var(--colorNeutralForeground3);
            }
            .warn {
                color: var(--colorPaletteDarkOrangeForeground1);
                margin-top: 6px;
            }
            .actions {
                display: flex;
                justify-content: flex-end;
                gap: 8px;
                margin-top: 10px;
            }
        `,
    ];

    /** The session's user messages, newest first. Passed in: the transcript belongs to cv-app, and
     *  core/ must not reach into the UI to read it. */
    @property({ attribute: false }) points: RewindPoint[] = [];

    @state() private _selected = '';
    @state() private _impact: RewindResultNotification | null = null;
    @state() private _probing = false;

    /** Uuids that have a file snapshot. Null until the host has answered — the list stays empty
     *  until then rather than showing everything and taking rows away a moment later. */
    @state() private _rewindable: Set<string> | null = null;

    override updated(changed: Map<string, unknown>): void {
        super.updated(changed);
        if (changed.has('open') && this.open) {
            void bridge
                .sendRequest(RewindPointsReq, {})
                .then((r) => {
                    this._rewindable = new Set(r.uuids ?? []);
                })
                .catch(() => {
                    // Could not tell which are rewindable: show them all rather than an empty
                    // dialog. The probe on the selected row still gives the real answer.
                    this._rewindable = null;
                });
        }
    }

    /** What the user typed in the filter box. Plain substring, case-insensitive — the prompts are
     *  their own words, so they are looked for as remembered, not fuzzy-matched. */
    @state() private _query = '';

    /** The rows to offer: the messages with a snapshot, narrowed by the filter. Falls back to all
     *  of them when the host could not say which are rewindable. */
    private get _rows(): RewindPoint[] {
        const known = this._rewindable;
        const base = known ? this.points.filter((p) => known.has(p.uuid)) : this.points;
        const q = this._query.trim().toLowerCase();
        return q ? base.filter((p) => p.text.toLowerCase().includes(q)) : base;
    }

    private _onPick(p: RewindPoint): void {
        if (this._selected === p.uuid) {
            return;
        }
        this._selected = p.uuid;
        this._impact = null;
        this._probing = true;
        // dry_run: the CLI answers what it WOULD change and writes nothing. Selecting a row has to
        // stay free of consequences, or the list cannot be browsed.
        void bridge
            .sendRequest(RewindReq, { messageUuid: p.uuid, dryRun: true })
            .then((r) => {
                // A slower answer for a row already left behind would overwrite the current one.
                if (this._selected === p.uuid) {
                    this._impact = r;
                }
            })
            .finally(() => {
                this._probing = false;
            });
    }

    /** One handler on the listbox rather than one per option: the rows are re-rendered on every
     *  keystroke in the filter, and a closure per row would be rebuilt each time. */
    private _onListClick(e: Event): void {
        const uuid = (e.target as HTMLElement)?.closest('fluent-option')?.getAttribute('value');
        const row = uuid && this._rows.find((p) => p.uuid === uuid);
        if (row) {
            this._onPick(row);
        }
    }

    /**
     * Arrow keys move the selection, Home/End jump to the ends.
     *
     * Written here rather than taken from a component: `fluent-listbox` styles and tracks
     * selection but does no keyboard navigation (only dropdown, radio-group and slider do), and
     * `fluent-dropdown`, which does, is a popup — inside a dialog whose list is already open that
     * is the wrong shape.
     */
    private _onListKey(e: KeyboardEvent): void {
        const rows = this._rows;
        if (!rows.length) {
            return;
        }
        const step = e.key === 'ArrowDown' ? 1 : e.key === 'ArrowUp' ? -1 : 0;
        let next: number;
        if (step !== 0) {
            const at = rows.findIndex((p) => p.uuid === this._selected);
            // No selection yet: Down takes the first, Up the last. Clamped, not wrapped — a list
            // that jumps from one end to the other loses you the sense of where you are.
            next =
                at < 0
                    ? step > 0
                        ? 0
                        : rows.length - 1
                    : Math.min(rows.length - 1, Math.max(0, at + step));
        } else if (e.key === 'Home') {
            next = 0;
        } else if (e.key === 'End') {
            next = rows.length - 1;
        } else {
            return;
        }
        e.preventDefault();
        const row = rows[next];
        this._onPick(row);
        // Scroll only — no focus to move: fluent-option carries no tabindex, so the focus stays on
        // whatever the user is typing in and the selected fill is what marks the row. Without this
        // the selection walks past the visible edge and the impact pane describes a row off screen.
        void this.updateComplete.then(() => {
            this.renderRoot
                .querySelector(`.row[data-uuid="${row.uuid}"]`)
                ?.scrollIntoView({ block: 'nearest' });
        });
    }

    /** Open VS's diff viewer on one file: its copy from before the message against what is on disk
     *  now. Only the path is sent — the host reads both sides itself. */
    private _openDiff(filePath: string): void {
        bridge.sendNotification<RewindDiffNotification>(Msg.fromWebView.session.rewindDiff, {
            messageUuid: this._selected,
            filePath,
        });
    }

    private _doRewind(): void {
        bridge.sendRequest(RewindReq, { messageUuid: this._selected, dryRun: false });
        this._close();
    }

    private _renderImpact() {
        if (!this._selected) {
            return html`<div class="impact muted">Select a message to see what would change.</div>`;
        }
        if (this._probing || !this._impact) {
            return html`<div class="impact muted">Checking…</div>`;
        }
        const i = this._impact;
        if (!i.canRewind) {
            return html`<div class="impact muted">${i.error || 'Nothing to restore here.'}</div>`;
        }
        const files = i.filesChanged ?? [];
        // Every user message is a valid target — that is the CLI's model, not an accident — so a
        // message that changed nothing answers canRewind:true with an empty list. Saying "the code
        // has not changed" is the answer; "0 files · +0 −0" would look like a failed read.
        if (!files.length) {
            return html`<div class="impact muted">
                The code has not changed since this message, so nothing would be restored.
            </div>`;
        }
        return html`<div class="impact">
            <div class="summary">
                ${files.length} file${files.length === 1 ? '' : 's'} ·
                <span class="ins">+${i.insertions}</span>
                <span class="del">−${i.deletions}</span>
            </div>
            <div class="files">
                ${files.map(
                    (f) =>
                        html`<button class="file" title=${f} @click=${() => this._openDiff(f)}>
                            ${displayPathUi(f)}
                        </button>`,
                )}
            </div>
            <!-- A count, not a list of paths: the CLI reports how many symlinks it left alone, not
                 which. Worth saying anyway — it is the one case where a rewind is partial. -->
            ${
                i.skippedLinks > 0
                    ? html`<div class="warn">
                          ${i.skippedLinks} symlink${i.skippedLinks === 1 ? '' : 's'} will not be
                          restored.
                      </div>`
                    : nothing
            }
        </div>`;
    }

    override render() {
        if (!this.open) {
            return nothing;
        }
        // Both conditions: the CLI can rewind to a message that changed nothing, and pressing a
        // button that would do nothing is how a feature earns a reputation for being broken.
        const canRewind = !!this._impact?.canRewind && !!this._impact.filesChanged?.length;
        const rows = this._rows;
        return html`
            <fluent-dialog type="modal" aria-label="Rewind to" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <h2 slot="title">Rewind to…</h2>
                    <fluent-button
                        slot="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Close"
                        @click=${() => this._close()}
                        >${unsafeHTML(Dismiss16Regular)}</fluent-button
                    >
                    <!-- The second sentence is not a detail: the CLI snapshots a file only when it
                         edits one through Write/Edit/NotebookEdit, so anything it did with a shell
                         command stays done. Without saying so, a rewind that leaves half the work
                         in place looks like a bug rather than the boundary it is. -->
                    <p class="hint">
                        Restore the files to how they were before a message — the conversation is
                        left alone. Only files Claude edited come back: what it changed by running a
                        command is not part of a checkpoint.
                    </p>
                    <!-- Arrow keys are handled on the wrapper, not on the list: typing in the box
                         and stepping through the results is one gesture, and moving the hand to
                         the list first would break it. -->
                    <!-- tabindex so the arrows reach it even with no filter box to type in: the
                         options carry no tabindex of their own, so without this there is nothing
                         between the dialog and the buttons for a key to land on. -->
                    <div class="picker" tabindex="0" @keydown=${this._onListKey}>
                        ${
                            this.points.length > 6
                                ? html`<fluent-text-input
                                      class="search"
                                      type="text"
                                      placeholder="Filter messages…"
                                      aria-label="Filter messages"
                                      .value=${this._query}
                                      @input=${(e: Event) => {
                                          this._query = (e.target as HTMLInputElement).value;
                                      }}
                                  ></fluent-text-input>`
                                : nothing
                        }
                        ${
                            rows.length
                                ? html`<fluent-listbox
                                      class="list"
                                      aria-label="Messages"
                                      @click=${this._onListClick}
                                  >
                                      ${rows.map(
                                          (p) =>
                                              html`<fluent-option
                                                  class="row"
                                                  value=${p.uuid}
                                                  data-uuid=${p.uuid}
                                                  ?selected=${this._selected === p.uuid}
                                                  title=${p.text}
                                              >
                                                  <!-- Both texts in the DEFAULT slot, so they share the
                                                   content cell and can be spread apart inside it.
                                                   The description slot would put the time on a row
                                                   of its own, under the label. -->
                                                  <span class="row-text"
                                                      >${p.text || '(empty message)'}</span
                                                  >
                                                  ${
                                                      p.timestamp
                                                          ? html`<cv-time-ago
                                                                class="row-when"
                                                                .ms=${p.timestamp}
                                                            ></cv-time-ago>`
                                                          : nothing
                                                  }
                                              </fluent-option>`,
                                      )}
                                  </fluent-listbox>`
                                : html`<div class="muted">
                                      ${
                                          this._query.trim()
                                              ? 'No message matches that.'
                                              : this._rewindable === null
                                                ? 'Looking for restore points…'
                                                : 'Nothing to rewind to yet — no files have been edited in this session.'
                                      }
                                  </div>`
                        }
                    </div>
                    ${this._renderImpact()}
                    <!-- No Cancel: the ✕ and Esc already close this, and a third way out would sit
                         beside the one button that does something. -->
                    <div class="actions">
                        <!-- The only thing here that writes to disk, and it stays a deliberate
                             press: clicking a row selects, it never restores. -->
                        <fluent-button
                            appearance="primary"
                            ?disabled=${!canRewind}
                            @click=${() => this._doRewind()}
                            >Rewind</fluent-button
                        >
                    </div>
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-rewind-dialog': CvRewindDialog;
    }
}

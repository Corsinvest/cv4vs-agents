/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type { SpinnerVerbsConfig } from '../../core/types';
import { state as appState } from '../../core/state';

// Verbs the spinner cycles; replace/extend via setVerbsConfig at runtime.
const DEFAULT_VERBS: readonly string[] = [
    'Cogitating',
    'Pondering',
    'Reflecting',
    'Reasoning',
    'Considering',
    'Contemplating',
    'Mulling',
    'Thinking',
    'Working',
    'Processing',
    'Analyzing',
    'Computing',
    'Crunching',
    'Investigating',
    'Exploring',
    'Inspecting',
    'Examining',
    'Reviewing',
    'Searching',
    'Looking',
    'Reading',
    'Parsing',
    'Resolving',
    'Drafting',
    'Composing',
    'Writing',
    'Sketching',
    'Building',
    'Assembling',
    'Crafting',
    'Refining',
    'Polishing',
    'Tuning',
    'Adjusting',
    'Iterating',
    'Recalibrating',
    'Tinkering',
    'Wrangling',
    'Untangling',
    'Spelunking',
    'Diving',
    'Surveying',
    'Mapping',
    'Linking',
    'Connecting',
    'Synthesizing',
    'Assembling',
    'Wiring',
    'Routing',
    'Negotiating',
    'Conferring',
    'Consulting',
    'Listening',
    'Watching',
    'Tracking',
    'Tracing',
    'Following',
    'Hunting',
    'Sniffing',
    'Foraging',
    'Pacing',
    'Stretching',
    'Loading',
    'Buffering',
    'Caching',
    'Indexing',
    'Sorting',
    'Aligning',
    'Stitching',
    'Tidying',
    'Polishing',
];

const STAR_GROW = ['·', '✢', '✳', '✶', '✻', '✽'];
const FRAMES: readonly string[] = [...STAR_GROW, ...[...STAR_GROW].reverse()];
const FRAME_INTERVAL_MS = 120;
const VERB_MIN_MS = 2000;
const VERB_MAX_MS = 5000;

let _activeVerbs: readonly string[] = DEFAULT_VERBS;

/**
 * Set the verb pool for every <cv-spinner>. 'replace' overrides defaults,
 * 'append' adds to them, `null` resets to defaults.
 */
export function setVerbsConfig(cfg: SpinnerVerbsConfig | null): void {
    if (!cfg) {
        _activeVerbs = DEFAULT_VERBS;
        return;
    }
    // 'replace' with an empty list falls back to defaults (matches VS Code).
    _activeVerbs =
        cfg.mode === 'replace'
            ? cfg.verbs.length > 0
                ? [...cfg.verbs]
                : DEFAULT_VERBS
            : [...DEFAULT_VERBS, ...cfg.verbs];
}

function pickRandomVerb(): string {
    return _activeVerbs[Math.floor(Math.random() * _activeVerbs.length)] ?? 'Working';
}

/**
 * Working indicator: cycles a braille frame ~120ms, randomises the verb
 * every few seconds. Timers cleared in disconnectedCallback (no leaks).
 *
 *   <cv-spinner></cv-spinner>
 */
@customElement('cv-spinner')
export class CvSpinner extends LitElement {
    static override styles = css`
        .wrap {
            align-self: flex-start;
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 6px 4px;
            color: var(--colorNeutralForeground3);
            font-size: 12px;
        }
        .icon {
            font-size: 14px;
            line-height: 1;
            display: inline-block;
            width: 16px;
            text-align: center;
        }
        .text {
            font-size: 12px;
            opacity: 0.8;
            min-width: 120px;
        }
        .dbg {
            font-size: 11px;
            opacity: 0.5;
            font-family: monospace;
        }
    `;

    /** Raw CLI work status (appState.status). Known values map to a fixed label that overrides the
     *  random verb; anything else (incl. "") falls back to the random working verb. */
    @property() status = '';

    /** Map a known CLI status to its spinner label. Add entries here as new statuses appear. */
    private static readonly STATUS_LABELS: Record<string, string> = {
        compacting: 'Compacting',
    };

    @state() private _frame = FRAMES[0];
    @state() private _verb = pickRandomVerb();

    private _frameTimer = 0;
    private _verbTimer = 0;

    override connectedCallback(): void {
        super.connectedCallback();
        let i = 0;
        this._frameTimer = window.setInterval(() => {
            i = (i + 1) % FRAMES.length;
            this._frame = FRAMES[i];
        }, FRAME_INTERVAL_MS);
        this._scheduleNextVerb();
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        clearInterval(this._frameTimer);
        clearTimeout(this._verbTimer);
    }

    private _scheduleNextVerb(): void {
        const delay = VERB_MIN_MS + Math.random() * (VERB_MAX_MS - VERB_MIN_MS);
        this._verbTimer = window.setTimeout(() => {
            this._verb = pickRandomVerb();
            this._scheduleNextVerb();
        }, delay);
    }

    override render() {
        // In DEBUG (developer under VS) append the raw CLI work status, to see what the CLI emits
        // (e.g. "requesting" during thinking) — never shown in Release.
        const dbg =
            appState.inDev && this.status
                ? html`<span class="dbg">status = ${this.status}</span>`
                : '';
        return html`
            <div class="wrap">
                <span class="icon">${this._frame}</span>
                <span class="text">${CvSpinner.STATUS_LABELS[this.status] || this._verb}…</span>
                ${dbg}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-spinner': CvSpinner;
    }
}

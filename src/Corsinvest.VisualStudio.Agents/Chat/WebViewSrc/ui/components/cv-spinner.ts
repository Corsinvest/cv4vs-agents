/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, property, state } from 'lit/decorators.js';
import type {
    AssistantTextDeltaNotification,
    SpinnerVerbsConfigDto,
    ThinkingDeltaNotification,
} from '../../core/types';
import { formatDurationSec, formatTokens } from '../helpers/format';
import { onTick, type UnsubscribeTick } from '../../core/tick';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';

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

/** Show the cycling verb next to the frame. Off: the animation already says "working", and a
 *  word that changes every few seconds draws the eye without telling anyone anything — the
 *  elapsed time does that better. A known status (Compacting) still gets its label either way.
 *  The pool and its CLI config stay wired up: flip this back to true to have them again. */
const SHOW_RANDOM_VERBS = false;

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
export function setVerbsConfig(cfg: SpinnerVerbsConfigDto | null): void {
    if (!cfg) {
        _activeVerbs = DEFAULT_VERBS;
        return;
    }
    // 'replace' with an empty list falls back to defaults: an empty verb pool would
    // leave the spinner with no label at all.
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
 * Working indicator: cycles a braille frame ~120ms plus the elapsed seconds, and a label for a
 * known CLI status (Compacting). The random verb is behind SHOW_RANDOM_VERBS, off today.
 * Timers cleared in disconnectedCallback (no leaks).
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
        /* Brand blue: with no verb beside it the frame is the only thing saying "still
           working", so it takes the accent rather than sitting in the muted body colour. */
        .icon {
            font-size: 14px;
            line-height: 1;
            display: inline-block;
            width: 16px;
            text-align: center;
            color: var(--colorBrandBackground);
        }
        /* The min-width reserves room for the longest verb, so the elapsed time doesn't jitter
           left and right as the word changes. Only worth it while a word is actually there. */
        .text {
            font-size: 12px;
            opacity: 0.8;
            min-width: 120px;
        }
        .elapsed,
        .tokens {
            font-size: 12px;
            opacity: 0.6;
            font-variant-numeric: tabular-nums;
        }
        /* The wrap's gap already separates the row's parts; this pulls the count back towards the
           seconds so the two read as one figure rather than as two unrelated badges. */
        .tokens {
            margin-left: -2px;
        }
    `;

    /** Raw CLI work status (appState.status). Known values get a fixed label; anything else
     *  (incl. "") leaves the frame to speak for itself — or falls back to the random verb
     *  while SHOW_RANDOM_VERBS is on. */
    @property() status = '';

    /** Map a known CLI status to its spinner label. Add entries here as new statuses appear. */
    private static readonly STATUS_LABELS: Record<string, string> = {
        compacting: 'Compacting',
    };

    @state() private _frame = FRAMES[0];
    @state() private _verb = pickRandomVerb();
    @state() private _elapsedSec = 0;
    /** Characters of answer text received so far this turn, turned into tokens at render. An
     *  estimate, and deliberately so: the real count only exists once a model call is finished
     *  (`message_delta`), which would leave the number frozen through the whole of a long answer.
     *  The CLI's own spinner does the same (`responseLengthRef.current / 4`). Lives here for the
     *  same reason the elapsed seconds do — born with the turn, dead with it, no reset needed. */
    @state() private _answerChars = 0;
    /** Thinking tokens, which the API DOES report as it goes (`estimated_tokens` on some
     *  thinking_delta frames). Added to the estimate above: both are output the user is waiting on. */
    @state() private _thinkingTokens = 0;

    private _frameTimer = 0;
    private _verbTimer = 0;
    private _offs: Array<() => void> = [];
    private _unsubscribeTick?: UnsubscribeTick;
    private _startedAt = 0;

    override connectedCallback(): void {
        super.connectedCallback();
        // Main thread only (parentToolUseId null): a sub-agent's own output is its chip's business,
        // and counting it here would make the number jump for work this line isn't measuring.
        this._offs.push(
            bridge.onNotification<AssistantTextDeltaNotification>(
                Msg.toWebView.chat.assistantTextDelta,
                (d) => {
                    if (d?.parentToolUseId) {
                        return;
                    }
                    this._answerChars += d?.text?.length ?? 0;
                },
            ),
            bridge.onNotification<ThinkingDeltaNotification>(
                Msg.toWebView.chat.thinkingDelta,
                (d) => {
                    // -1 marks a frame that carries no estimate; most of them don't.
                    if (d?.parentToolUseId || !d || d.estimatedTokens < 0) {
                        return;
                    }
                    this._thinkingTokens += d.estimatedTokens;
                },
            ),
        );
        let i = 0;
        // The frames stay on their own timer: at 120ms they are an animation, and rounding them to
        // the shared second would turn the spinner into a stutter.
        this._frameTimer = window.setInterval(() => {
            i = (i + 1) % FRAMES.length;
            this._frame = FRAMES[i];
        }, FRAME_INTERVAL_MS);
        this._scheduleNextVerb();
        // performance.now(): monotonic, so a system-clock change mid-turn can't make the counter
        // jump or go backwards. The element is created when the turn starts and destroyed when it
        // ends, so mount time IS the turn start and needs no reset.
        //
        // The shared tick only says WHEN to recompute; the value still comes from performance.now(),
        // so the monotonic guarantee survives while this second counts in step with the elapsed
        // badges beside it instead of drifting against them.
        this._startedAt = performance.now();
        this._unsubscribeTick = onTick(() => {
            this._elapsedSec = Math.floor((performance.now() - this._startedAt) / 1000);
        });
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        clearInterval(this._frameTimer);
        clearTimeout(this._verbTimer);
        this._offs.forEach((off) => off());
        this._offs = [];
        this._unsubscribeTick?.();
        this._unsubscribeTick = undefined;
    }

    private _scheduleNextVerb(): void {
        // Nothing reads _verb while the flag is off — re-rolling it would be a timer per pane
        // waking up to change something invisible.
        if (!SHOW_RANDOM_VERBS) {
            return;
        }
        const delay = VERB_MIN_MS + Math.random() * (VERB_MAX_MS - VERB_MIN_MS);
        this._verbTimer = window.setTimeout(() => {
            this._verb = pickRandomVerb();
            this._scheduleNextVerb();
        }, delay);
    }

    override render() {
        // Hidden for the first second: a turn that ends instantly would flash a "0s" that
        // reads as an error rather than as timing.
        const elapsed =
            this._elapsedSec > 0
                ? html`<span class="elapsed">${formatDurationSec(this._elapsedSec)}</span>`
                : nothing;
        // A known status always speaks; the random verb only when the flag lets it.
        const label = CvSpinner.STATUS_LABELS[this.status] || (SHOW_RANDOM_VERBS ? this._verb : '');
        // ~4 chars per token, the ratio the CLI's own spinner uses. An estimate: the exact figure
        // arrives only when a model call ends, and waiting for it would leave this frozen for the
        // whole of a long answer — the one moment it has something to say.
        const estimated = Math.round(this._answerChars / 4) + this._thinkingTokens;
        // After the seconds: the time is what the eye checks first, and this only starts moving
        // once the model writes. `↓` for output, like the end-of-turn row and the CLI.
        const tokens = estimated
            ? html`<span class="tokens">↓ ${formatTokens(estimated)} tok</span>`
            : nothing;
        return html`
            <div class="wrap">
                <span class="icon">${this._frame}</span>
                ${label ? html`<span class="text">${label}…</span>` : nothing} ${elapsed}${tokens}
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-spinner': CvSpinner;
    }
}

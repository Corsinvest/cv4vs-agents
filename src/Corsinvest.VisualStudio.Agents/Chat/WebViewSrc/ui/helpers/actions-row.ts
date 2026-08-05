/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The bottom actions row shared by a normal response (cv-app) and a sub-agent transcript
// (cv-tool-row): a Copy button, an optional Read-aloud button, and the "x ago" timestamp. Pure
// render — no state, no component.

import { html, nothing, type TemplateResult } from 'lit';
import type { ContextUsageDto } from '../../core/types';
import { formatTokens, formatDuration } from './format';
import '../components/cv-copy-btn';
import '../components/cv-speak-btn';

/** What one finished turn cost. Gathered from `result`, which is the only message that carries
 *  the final figures. */
export interface TurnMetrics {
    costUsd: number;
    durationMs: number;
    usage: ContextUsageDto | null;
}

/** "$0.3260 · ↑ 2 ↓ 84 tok · 3.2s". The figures carry `.v`, the $ ↑ ↓ tok s markers around them
 *  don't: at this size everything at one weight reads as a single grey run with no way in, so the
 *  numbers are what the eye should land on and the markers only say which is which.
 *
 *  Cache reads are deliberately left out of the input count: they are context replayed, not tokens
 *  this turn spent, and counting them puts a five-figure number next to an answer that cost two.
 *  Every part is dropped when zero rather than shown as "0" — a replayed turn has its token counts
 *  but no cost or duration (those ride `result`, which the JSONL has no line for), so it renders
 *  the tokens alone instead of "$0 · … · 0.0s". */
function formatMetrics(m: TurnMetrics) {
    const parts: TemplateResult[] = [];
    if (m.costUsd) {
        parts.push(html`$<span class="v">${m.costUsd.toFixed(4)}</span>`);
    }
    const inTok = m.usage?.inputTokens ?? 0;
    const outTok = m.usage?.outputTokens ?? 0;
    if (inTok || outTok) {
        parts.push(
            html`${inTok ? html`↑ <span class="v">${formatTokens(inTok)}</span> ` : nothing}${
                outTok ? html`↓ <span class="v">${formatTokens(outTok)}</span> ` : nothing
            }tok`,
        );
    }
    if (m.durationMs) {
        parts.push(html`<span class="v">${formatDuration(m.durationMs)}</span>`);
    }
    // join() would stringify the templates ("[object Object]"); interleave the separator instead.
    return parts.flatMap((p, i) => (i ? [html` · `, p] : [p]));
}

/** Copy button (+ optional Read-aloud when `speak`) + "x ago" timestamp, and — when the turn's
 *  figures are known and the option is on — cost/tokens/duration pushed to the far end. Hover-gating
 *  is CSS: `.cv-response-actions` has base opacity 0 and a hover rule on the container reveals it —
 *  pass that container's reveal class in `extraClass`. ts=0 hides the timestamp. `speak` adds the
 *  TTS button (prose answers want it; a tool transcript doesn't). */
export function renderActionsRow(
    text: string,
    ts: number,
    title: string,
    extraClass = '',
    speak = false,
    metrics: TurnMetrics | null = null,
) {
    return html`
        <div class="cv-response-actions ${extraClass}">
            <cv-copy-btn .text=${text} title=${title}></cv-copy-btn>
            ${speak ? html`<cv-speak-btn .text=${text}></cv-speak-btn>` : nothing}
            ${ts > 0 ? html`<cv-time-ago .ms=${ts}></cv-time-ago>` : nothing}
            ${metrics ? html`<span class="cv-turn-metrics">${formatMetrics(metrics)}</span>` : nothing}
        </div>
    `;
}

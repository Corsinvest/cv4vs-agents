/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Context-window arithmetic + model-label helpers. Model metadata (the catalogue, effort
// levels, context window) is NOT stored here — it comes from the CLI at runtime
// via `chat_models` and the result's modelUsage (see state.models /
// state.contextWindow). No static model table. How a token count is WRITTEN is display, not
// arithmetic: that lives in ui/helpers/format.

import type { ContextUsageDto } from './types';
import { state as appState } from './state';

/** Resolve a served model id to a catalogue `value`. The CLI reports the served id
 *  (e.g. `claude-opus-4-8[1m]`), which the catalogue keys (`default`, `opus[1m]`, …)
 *  don't match directly — but each catalogue entry carries a `resolvedModel` (the served id
 *  it maps to). We match on that (exact, then without the [1m] suffix), preferring a NAMED entry
 *  over `default` when several share the same resolvedModel. No family-name guessing, so
 *  alternative providers work too. */
export function resolveModelValue(value: string | null | undefined): string {
    if (!value || value === 'default') {
        return 'default';
    }
    const models = appState.models;
    // Match the served id against the catalogue by the explicit `resolvedModel` field, not by
    // guessing the family from the name (which breaks for env-var-remapped providers like
    // GLM/z.ai). `value` may itself carry a [1m] suffix.
    // Strip the [1m] (1M-context) suffix from BOTH sides: the served id in the transcript may
    // lack it (`claude-opus-4-8`) while the catalogue's resolvedModel carries it
    // (`claude-opus-4-8[1m]`), so compare the bare forms too.
    const strip = (s: string): string => s.replace(/\[1m\]$/i, '');
    const bare = strip(value);
    const rm = (m: { resolvedModel?: string }): string => strip(m.resolvedModel ?? '');
    const hit =
        models.find((m) => m.value === value) ??
        models.find((m) => m.value === bare) ??
        // Several entries can share a resolvedModel (default and opus both point at the same opus
        // id). Prefer the NAMED one: a served id says which model ran, not whether it was reached
        // through `default`, so naming the model is the claim it can back.
        models.find((m) => m.value !== 'default' && rm(m) === bare) ??
        models.find((m) => rm(m) === bare);
    return hit?.value ?? (models.some((m) => m.value === 'default') ? 'default' : value);
}

/** Trigger-button label for the current model id. Prefers the CLI's displayName;
 *  falls back to a trimmed id when the catalogue hasn't arrived yet. */
export function modelLabel(value: string | null | undefined): string {
    const resolved = resolveModelValue(value);
    const m = appState.models.find((x) => x.value === resolved);
    if (m) {
        return m.displayName;
    }
    if (resolved === 'default') {
        return 'Default';
    }
    return resolved;
}

/** Short form of modelLabel() for the composer toolbar, where the full name ("Opus (1M context)",
 *  "Default (recommended)") would eat the row. Drops a trailing parenthesised qualifier and any
 *  [1m] tag — a shape rule, not a name list, so provider-supplied labels shorten too. Falls back to
 *  the full label when the trim would leave nothing. Pair it with the full name in a title. */
export function modelLabelShort(value: string | null | undefined): string {
    const full = modelLabel(value);
    const short = full
        .replace(/\s*\[1m\]/i, '')
        .replace(/\s*\([^)]*\)\s*$/, '')
        .trim();
    return short || full;
}

/** Tokens that occupy the prompt window. `output_tokens` is GENERATED,
 *  not part of the input — it doesn't count towards the limit. */
export function consumedTokens(u: ContextUsageDto): number {
    return u.inputTokens + u.cacheReadTokens + u.cacheCreationTokens;
}

const CACHE_TTL_MS: Record<string, number> = { '5m': 5 * 60 * 1000, '1h': 60 * 60 * 1000 };

export type CacheState =
    | { kind: 'unknown' }
    | { kind: 'warm'; minutesLeft: number }
    | { kind: 'cold'; idleMs: number; recacheTokens: number };

/** An estimate, which is why the UI says "likely": the API states the TTL it wrote but never
 *  reports whether the entry is still live, so anchor + TTL against the clock is all there is. */
export function cacheState(
    u: ContextUsageDto | null,
    anchorMs: number | null,
    now: number,
): CacheState {
    const ttl = u?.cacheTtl ? CACHE_TTL_MS[u.cacheTtl] : undefined;
    if (!u || !anchorMs || ttl === undefined) {
        return { kind: 'unknown' };
    }
    const msLeft = anchorMs + ttl - now;
    return msLeft > 0
        ? { kind: 'warm', minutesLeft: Math.ceil(msLeft / 60000) }
        : { kind: 'cold', idleMs: now - anchorMs, recacheTokens: consumedTokens(u) };
}

/** Percent of the context window consumed. 0 until the window is known (no
 *  result yet). Clamped to [0, 100] — the CLI can report >100 on batches. */
export function contextPercent(u: ContextUsageDto): number {
    const limit = appState.contextWindow;
    if (limit <= 0) {
        return 0;
    }
    return Math.min(100, Math.max(0, (consumedTokens(u) / limit) * 100));
}

/** Buffer the CLI keeps free on top of the output reservation, so our gauge
 *  reaches 100% at the point the CLI actually auto-compacts. */
const AUTO_COMPACT_RESERVE = 13_000;

/** Usable window before auto-compaction: contextWindow − maxOutput − buffer.
 *  0 until the window is known. */
export function autoCompactWindow(): number {
    const limit = appState.contextWindow;
    if (limit <= 0) {
        return 0;
    }
    return Math.max(1, limit - appState.maxOutputTokens - AUTO_COMPACT_RESERVE);
}

/** Percent of the auto-compact window still free ("{n}% of context remaining
 *  until auto-compact"). Clamped to [0, 100]. */
export function remainingPercent(u: ContextUsageDto): number {
    const window = autoCompactWindow();
    if (window <= 0) {
        return 100;
    }
    const usedPct = (consumedTokens(u) / window) * 100;
    return Math.min(100, Math.max(0, 100 - usedPct));
}

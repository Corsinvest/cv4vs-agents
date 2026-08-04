/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Token accounting + model-label helpers. Model metadata (the catalogue, effort
// levels, context window) is NOT stored here — it comes from the CLI at runtime
// via `chat_models` and the result's modelUsage (see state.models /
// state.contextWindow). No static model table.

import type { ContextUsageDto } from './types';
import { state as appState } from './state';

/** Resolve a served model id to a catalogue `value`. The CLI reports the served id
 *  (e.g. `claude-opus-4-8[1m]`), which the catalogue keys (`default`, `opus[1m]`, …)
 *  don't match directly — but each catalogue entry carries a `resolvedModel` (the served id
 *  it maps to). We match on that (exact, then without the [1m] suffix), preferring a NAMED entry
 *  over `default` when several share the same resolvedModel — the picker hides that duplicated
 *  `default` (see displayModels). No family-name guessing, so alternative providers work too. */
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
        // Several entries can share a resolvedModel (default and opus[1m] both point at the same
        // opus id). Prefer the NAMED one over `default`: the picker hides a duplicated `default`
        // (see displayModels), so resolving to it would leave no row checked.
        models.find((m) => m.value !== 'default' && rm(m) === bare) ??
        models.find((m) => rm(m) === bare);
    return hit?.value ?? (models.some((m) => m.value === 'default') ? 'default' : value);
}

/** The catalogue as the picker should SHOW it: `default` is dropped when another entry resolves to
 *  the same model, because the two are then the same choice under two names — and picking either
 *  writes the same served id to the transcript, so a resumed session can no longer tell them apart
 *  (the picker would say "Default" for an explicit "Opus"). Claude Desktop sidesteps this by never
 *  listing a Default row at all; we only hide it when it IS a duplicate.
 *
 *  Kept conditional on the data, never on a name: if `default` resolves to something no other entry
 *  offers (plausible on an env-remapped provider), it stays — dropping it would make that model
 *  unreachable. The surviving twin inherits the "(recommended)" hint so nothing is lost.
 *
 *  Filters the DISPLAY list only — `appState.models` keeps every entry, so lookups by value
 *  (currentModelInfo, effort levels) still resolve for sessions that stored `default`. */
export function displayModels<
    T extends { value: string; resolvedModel?: string; description: string },
>(models: readonly T[]): T[] {
    const def = models.find((m) => m.value === 'default');
    if (!def) {
        return [...models];
    }
    const strip = (s: string): string => s.replace(/\[1m\]$/i, '');
    const target = strip(def.resolvedModel ?? '');
    const twin = target
        ? models.find((m) => m.value !== 'default' && strip(m.resolvedModel ?? '') === target)
        : undefined;
    if (!twin) {
        return [...models];
    }
    return models
        .filter((m) => m.value !== 'default')
        .map((m) => (m === twin ? { ...m, description: def.description || m.description } : m));
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

/** Compact token formatter for tooltips: 12 → "12", 1500 → "1.5k",
 *  357000 → "357k", 1_200_000 → "1.2M". */
export function formatTokens(n: number): string {
    if (n >= 1_000_000) {
        return (n / 1_000_000).toFixed(1) + 'M';
    }
    if (n >= 1_000) {
        return (n / 1_000).toFixed(0) + 'k';
    }
    return String(n);
}

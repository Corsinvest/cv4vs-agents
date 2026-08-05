/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Display-oriented formatting helpers (pure, no DOM). Output shape is
// user-facing, hence ui/ rather than core/.
//
// Everything the chat writes as a number-for-a-human lives here, so the same quantity reads the
// same way wherever it appears: token counts (spinner, response row, thinking, sub-agents, the
// context dialog) and durations (turn timer, tool rows, sub-agent chips). They used to be four
// near-identical private helpers, each with its own rounding.

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

const UNITS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
    ['year', 365 * 24 * 3600_000],
    ['month', 30 * 24 * 3600_000],
    ['week', 7 * 24 * 3600_000],
    ['day', 24 * 3600_000],
    ['hour', 3600_000],
    ['minute', 60_000],
    ['second', 1000],
];

/**
 * Truncate text and append a newline + ellipsis when over `max` chars.
 */
export function truncate(s: string | undefined | null, max: number): string {
    if (!s) {
        return '';
    }
    return s.length <= max ? s : s.slice(0, max) + '\n…';
}

/** "20 minutes ago", "2 hours ago", "yesterday" — localized. `now` is injectable so the caller
 *  can re-render the same stamp against a later clock without reaching into the DOM. */
export function formatTimeAgo(ms: number, now: number = Date.now()): string {
    const diff = ms - now; // negative = in the past
    const abs = Math.abs(diff);
    for (const [unit, size] of UNITS) {
        if (abs >= size || unit === 'second') {
            return rtf.format(Math.round(diff / size), unit);
        }
    }
    return rtf.format(0, 'second');
}

/** Full localized date + time, for the tooltip. */
export function formatAbsolute(ms: number): string {
    return new Date(ms).toLocaleString();
}

/** How long something took, in the one shape the chat uses everywhere — the turn spinner, the
 *  response row, a thinking block, a sub-agent, a tool: "0.2s", "45s", "1m 23s". The decimal shows
 *  only below one second, so a 200ms turn doesn't read as "0s" while a counter ticking by the
 *  second never shows a pointless ".0". Not localized on purpose: these sit inside dense metric
 *  rows where "1m 23s" has to stay short and predictable, unlike the prose of formatTimeAgo. */
export function formatDuration(ms: number): string {
    if (ms < 1000) {
        return `${(ms / 1000).toFixed(1)}s`;
    }
    const s = Math.round(ms / 1000);
    return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}

/** Same, for the callers that already hold seconds (tool rows, the spinner's own counter). */
export function formatDurationSec(sec: number): string {
    return formatDuration(sec * 1000);
}

/** Compact token formatter: 12 → "12", 1500 → "1.5k", 357000 → "357k", 1_200_000 → "1.2M". Bare,
 *  for the places that label the number themselves — a table with a "Tokens" column header, or a
 *  tooltip already naming what it counts. */
export function formatTokens(n: number): string {
    if (n >= 1_000_000) {
        return (n / 1_000_000).toFixed(1) + 'M';
    }
    if (n >= 1_000) {
        return (n / 1_000).toFixed(0) + 'k';
    }
    return String(n);
}

/** A token count where the unit has to travel with it: "84 tok", "1.2k tok". `estimated` prefixes a
 *  tilde — the CLI measures the response counts but only estimates the thinking ones, and the two
 *  sit close enough together that they have to be told apart. */
export function formatTokenCount(n: number, estimated = false): string {
    return `${estimated ? '~' : ''}${formatTokens(n)} tok`;
}

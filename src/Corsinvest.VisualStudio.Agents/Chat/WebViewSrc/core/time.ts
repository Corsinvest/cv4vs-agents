/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// Human "time ago" + absolute date/time for message action rows. Uses the platform Intl APIs
// (browser locale). Formatting only, no rendering: the stamp that refreshes itself on hover is
// `ui/components/cv-time-ago`, which calls these.

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

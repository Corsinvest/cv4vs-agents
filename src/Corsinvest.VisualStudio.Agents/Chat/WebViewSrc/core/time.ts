/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// Human "time ago" + absolute date/time for message action rows. Uses the platform Intl APIs
// (browser locale). The relative form is computed once per render — no ticking timer.

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

/** "20 minutes ago", "2 hours ago", "yesterday" — localized. */
export function formatTimeAgo(ms: number): string {
    const diff = ms - Date.now(); // negative = in the past
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

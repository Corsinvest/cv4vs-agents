/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// Human "time ago" + absolute date/time for message action rows. Uses the platform Intl APIs
// (browser locale). The relative form is computed on render and refreshed on hover — see
// renderTimeAgo; there is no ticking timer.

import { html, type TemplateResult } from 'lit';

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

/** The "x ago" stamp for an action row, refreshing itself when the pointer arrives.
 *
 *  Lit renders on property change, and elapsed time is not a property — so a stamp rendered when
 *  the message arrived said "1 second ago" for as long as the message stayed on screen. The action
 *  row is always in the DOM and only revealed by `opacity` on hover, so the hover alone changed
 *  nothing: no re-render came with it.
 *
 *  Writing the text straight onto the element on `mouseenter` is enough, and is why this is a plain
 *  function rather than a timer: the stamp is only legible while the row is revealed, and the row is
 *  only revealed under the pointer. A ticking timer would instead keep hundreds of history messages
 *  re-rendering to move "2 hours ago" to "3 hours ago", which nobody is reading. */
export function renderTimeAgo(ms: number): TemplateResult {
    return html`<span
        class="cv-ts"
        title=${formatAbsolute(ms)}
        @mouseenter=${(e: Event) => {
            (e.currentTarget as HTMLElement).textContent = formatTimeAgo(ms);
        }}
        >${formatTimeAgo(ms)}</span
    >`;
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Display-oriented formatting helpers (pure, no DOM). Output shape is
// user-facing, hence ui/ rather than core/.

/**
 * Truncate text and append a newline + ellipsis when over `max` chars.
 */
export function truncate(s: string | undefined | null, max: number): string {
    if (!s) {
        return '';
    }
    return s.length <= max ? s : s.slice(0, max) + '\n…';
}

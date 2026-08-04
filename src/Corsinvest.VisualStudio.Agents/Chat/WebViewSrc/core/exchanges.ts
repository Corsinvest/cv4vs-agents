// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import type { UiEntry } from './types';

/**
 * Group a transcript into exchanges: each user message opens one, and whatever precedes the first
 * user message (a history page boundary) gets its own leading group.
 *
 * Pure and derived — cv-app calls this from a memoised getter instead of holding the groups as
 * state, so the groups can never drift from the entries they are built from.
 */
export function buildGroups(entries: readonly UiEntry[]): UiEntry[][] {
    const groups: UiEntry[][] = [];
    let current: UiEntry[] = [];
    for (const e of entries) {
        if (e.kind === 'text' && e.role === 'user') {
            if (current.length) {
                groups.push(current);
            }
            current = [e];
        } else {
            current.push(e);
        }
    }
    if (current.length) {
        groups.push(current);
    }
    return groups;
}

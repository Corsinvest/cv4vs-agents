// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import type { UiEntry } from './types';

/**
 * Group a transcript into exchanges: each user message opens one, and whatever precedes the first
 * user message (a history page boundary) gets its own leading group.
 *
 * A message still in the queue is the exception — it heads no turn, since the CLI has not been
 * given it. Opening an exchange for it would end the running turn's <section> early, and the
 * sticky user bubble pins only within its own section (.cv-exchange in chat.css): that turn's
 * header would come unstuck while its reply is still arriving. It opens its own group once sent.
 *
 * Pure and derived — cv-app calls this from a memoised getter instead of holding the groups as
 * state, so the groups can never drift from the entries they are built from.
 */
export function buildGroups(
    entries: readonly UiEntry[],
    queued?: ReadonlySet<string>,
): UiEntry[][] {
    const isQueued = (uuid?: string): boolean => !!uuid && !!queued?.has(uuid);
    const opensExchange = (e: UiEntry): boolean =>
        e.kind === 'text' && e.role === 'user' && !isQueued(e.uuid);

    const groups: UiEntry[][] = [];
    let current: UiEntry[] = [];
    for (const e of entries) {
        if (opensExchange(e)) {
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/** Bookkeeping for the `@` picker's requests. The host lists the workspace once per opening of the
 *  picker (`refresh`) and filters that listing for every later keystroke, so the first request of an
 *  opening must say so, and only the latest answer may land. */
export class AtRequests {
    private seq = 0;
    private listed = false;

    /** The next request to send, or null when the picker is closed: a debounced keystroke firing
     *  after Enter or Esc must neither start a listing nor use up the next opening's refresh. */
    next(open: boolean): { seq: number; refresh: boolean } | null {
        if (!open) {
            return null;
        }
        const refresh = !this.listed;
        this.listed = true;
        return { seq: ++this.seq, refresh };
    }

    /** False for an answer that a newer request has already superseded. */
    isLatest(seq: number): boolean {
        return seq === this.seq;
    }

    /** The picker closed: the next opening lists the workspace again. */
    closed(): void {
        this.listed = false;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/**
 * Which permission request is being answered, and which are waiting.
 *
 * The CLI can have several `can_use_tool` in flight at once — a turn firing parallel tools, or a
 * sub-agent asking while the main thread does — and the client tracks them all, keyed by
 * tool_use_id (`ClaudeClient._toolRequestIds`, a ConcurrentDictionary). The banner used to hold
 * one: a second request overwrote the first, which then vanished from screen while the CLI still
 * waited for an answer that could no longer be given, hanging that tool until the turn was
 * interrupted.
 *
 * Kept out of the component because it is state, not rendering, and this way it can be tested
 * without a DOM. The component owns what a request LOOKS like; this owns which one is up.
 */

/** The queue only ever needs the id — the component keeps the full request. */
export interface Identified {
    id: string;
}

export class PermissionQueue<T extends Identified> {
    private _current: T | null = null;
    private _waiting: T[] = [];

    /** The request being answered, or null when nothing is pending. */
    get current(): T | null {
        return this._current;
    }

    /** Those still waiting, oldest first. A copy: callers must not reorder the queue by hand. */
    get waiting(): readonly T[] {
        return this._waiting;
    }

    get size(): number {
        return (this._current ? 1 : 0) + this._waiting.length;
    }

    /**
     * Take a request. Returns true when it goes straight on screen, false when it queues behind
     * one already there — the caller uses that to decide whether to reset its per-request state.
     *
     * An id already known is ignored: the CLI re-sends a request whose turn was replayed, and
     * answering the same tool_use_id twice is worse than answering it once.
     */
    push(req: T): boolean {
        if (this._current?.id === req.id || this._waiting.some((q) => q.id === req.id)) {
            return false;
        }
        if (this._current) {
            this._waiting = [...this._waiting, req];
            return false;
        }
        this._current = req;
        return true;
    }

    /**
     * The current request is answered: promote the next one, or go empty. Returns what is now on
     * screen, null when the queue has run dry.
     */
    next(): T | null {
        const [head, ...rest] = this._waiting;
        this._waiting = rest;
        this._current = head ?? null;
        return this._current;
    }

    /**
     * Drop `id` wherever it sits. Returns the request now on screen — unchanged when the dropped
     * one was merely waiting, the promoted one when it was current, null when nothing is left.
     *
     * A queued request has to be droppable, not just the current one: the CLI cancels a
     * superseded `can_use_tool` by id, and a tool_result can arrive for one nobody answered.
     * Left in, it would surface later asking about a tool that has already run.
     */
    drop(id: string): T | null {
        if (this._current?.id === id) {
            return this.next();
        }
        this._waiting = this._waiting.filter((q) => q.id !== id);
        return this._current;
    }

    /** Session gone: everything waiting belonged to it and none of it can be answered now. */
    clear(): void {
        this._current = null;
        this._waiting = [];
    }
}

// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import type { UiEntry, UiToolEntry } from './types';

/** Chain of toolUseId from the root down to a nested entry; empty for a top-level one. */
type EntryPath = string[];

/** Replace the child sharing `key(entry)`, or append when there is none. */
function upsert(list: UiEntry[], entry: UiEntry, key: (e: UiEntry) => string): UiEntry[] {
    const k = key(entry);
    const i = list.findIndex((e) => key(e) === k);
    if (i < 0) {
        return [...list, entry];
    }
    const next = [...list];
    next[i] = entry;
    return next;
}

/**
 * Owns the chat transcript and updates it without ever mutating an entry.
 *
 * Lit compares properties by reference, so a mutated object never triggers an update. Every
 * mutating method here replaces the entry it touches AND rebuilds every object on the path from
 * the root down to it — the entries array, each ancestor's `children`, `children.items`. Branches
 * that did not change keep their identity, so Lit skips them. By construction, not by convention:
 * no caller has to remember to re-create the path itself.
 */
export class Transcript {
    private _entries: UiEntry[] = [];
    /** id → path, so find/update are O(1) instead of walking the tree on every event. */
    private _index = new Map<number, EntryPath>();

    /**
     * Called with the ids that just left the tree, subtrees included.
     *
     * Retraction means an id can disappear mid-session, and a key left behind outside this class
     * is either events written into an entry that is gone or a Map that never shrinks. Rather than
     * expect every caller to remember the cleanup, the removal announces itself — this class still
     * knows nothing about who listens or what they keyed.
     */
    onRemoved?: (ids: readonly number[]) => void;

    /**
     * Called when every id in the tree stops being valid at once — a session switch or a history
     * page swapped in.
     *
     * Separate from onRemoved rather than passing the whole id list: the listener only ever wants
     * to drop everything, so walking the tree to hand it an array it would immediately ignore buys
     * nothing. Two callbacks, two meanings — some ids died, or all of them did.
     */
    onCleared?: () => void;

    get entries(): readonly UiEntry[] {
        return this._entries;
    }

    append(entry: UiEntry): void {
        this._entries = [...this._entries, entry];
        this._index.set(entry.id, []);
    }

    find(id: number): UiEntry | null {
        const path = this._index.get(id);
        return path ? this._walk(path, id) : null;
    }

    clear(): void {
        this._entries = [];
        this._index.clear();
        this.onCleared?.();
    }

    /**
     * Drop the top-level entries whose `uuid` is in `uuids`, returning how many went. Used when the
     * CLI retracts messages it had already delivered (refusal fallback): they are no longer part of
     * the conversation, and leaving them on screen makes what the user reads diverge from what the
     * model knows.
     *
     * Matching is uuid equality and nothing else, so it is safe to apply blind: a uuid naming
     * nothing removes nothing, and calling it twice with the same list is the same as calling it
     * once. That is the contract the CLI relies on — the per-message `supersedes` and the
     * end-of-turn `retracted_message_uuids` overlap on purpose.
     *
     * At every depth, not just the top: a uuid names a MESSAGE, not a position, and a sub-agent's
     * reply is as retracted as a main-thread one. Two entries sharing a uuid are the same message
     * written twice (measured: the .jsonl does that on ~0.1% of assistant lines, identical on every
     * other field), so taking both is right rather than over-eager.
     */
    removeByUuid(uuids: readonly string[]): number {
        if (uuids.length === 0) {
            return 0;
        }
        const drop = new Set(uuids);
        const doomed: UiEntry[] = [];
        const prune = (list: readonly UiEntry[]): UiEntry[] | null => {
            let changed = false;
            const kept: UiEntry[] = [];
            for (const e of list) {
                if ('uuid' in e && typeof e.uuid === 'string' && drop.has(e.uuid)) {
                    doomed.push(e);
                    changed = true;
                    continue;
                }
                // Rebuild the parent only when a descendant actually went, so an untouched branch
                // keeps its identity and Lit skips it.
                if (e.kind === 'tool' && e.children) {
                    const kids = prune(e.children.items);
                    if (kids) {
                        kept.push({ ...e, children: { ...e.children, items: kids } });
                        changed = true;
                        continue;
                    }
                }
                kept.push(e);
            }
            return changed ? kept : null;
        };
        const next = prune(this._entries);
        if (!next || doomed.length === 0) {
            return 0;
        }
        this._entries = next;
        // Rebuild rather than unset the removed ids: a dropped entry takes its whole subtree with
        // it, and those children are indexed too.
        this._reindex();
        // Report the subtrees, not just the roots: whoever keyed something on a nested row is as
        // stranded as whoever keyed it on the message that contained it.
        const gone: number[] = [];
        const collect = (list: readonly UiEntry[]): void => {
            for (const e of list) {
                gone.push(e.id);
                if (e.kind === 'tool' && e.children?.items.length) {
                    collect(e.children.items);
                }
            }
        };
        collect(doomed);
        this.onRemoved?.(gone);
        return doomed.length;
    }

    /**
     * Move the top-level entry with `uuid` to the end.
     *
     * A message typed during a turn is echoed at once but only sent when that turn ends, so its
     * bubble sits above a reply it did not prompt — and `buildGroups` opens an exchange on every
     * user message, so that reply is grouped under the wrong question. Moving it on dispatch also
     * matches the order the .jsonl records, so the live view and a reopened session agree.
     *
     * Top level only: a user message is never nested under a tool row.
     */
    moveToEnd(uuid: string): boolean {
        const i = this._entries.findIndex((e) => 'uuid' in e && e.uuid === uuid);
        if (i < 0 || i === this._entries.length - 1) {
            return false;
        }
        const moved = this._entries[i];
        this._entries = [...this._entries.slice(0, i), ...this._entries.slice(i + 1), moved];
        this._reindex();
        return true;
    }

    /**
     * Replace the entry with `id` by `fn(entry)`, rebuilding every object on the path down to it.
     *
     * Returns false when the id is no longer in the tree — an async callback (a sub-agent fetch, a
     * compact summary) can resolve after /clear or a session switch, and must then do nothing.
     */
    update<T extends UiEntry>(id: number, fn: (e: T) => T): boolean {
        const path = this._index.get(id);
        if (!path) {
            return false;
        }
        // The caller names the member it expects (update<UiToolEntry>); the walk below is
        // type-blind, so the callback is widened for it.
        const before = this.find(id);
        const next = this._replace(this._entries, path, 0, id, (e) => fn(e as T));
        if (!next) {
            return false;
        }
        this._entries = next;
        // A caller may hand over a whole new children list rather than append one at a time —
        // "Show all" swaps the kept three for the fetched transcript. Those entries never went
        // through appendChild, so nothing indexed them: a live event for one of them would find
        // no path, or the stale path of the child it replaced, and update the wrong branch.
        const after = this.find(id);
        if (
            before?.kind === 'tool' &&
            after?.kind === 'tool' &&
            before.children?.items !== after.children?.items
        ) {
            this._reindex();
        }
        return true;
    }

    /** Append `delta` to a text entry. Same path rebuild as update(), used by the per-token
     *  stream: the cost is the tree depth, not the transcript length. */
    appendText(id: number, delta: string): boolean {
        return this.update<UiEntry>(id, (e) =>
            'text' in e ? ({ ...e, text: e.text + delta } as UiEntry) : e,
        );
    }

    /**
     * Append `entry` under the tool row `parentToolUseId`. A collapsed row keeps a ring of the
     * last three children and flags hasMore; showAll keeps the whole list and upserts, so a
     * re-emitted row (pending → done) updates in place instead of duplicating.
     *
     * `upsertKey` comes from the caller: what makes two children the same row is a presentation
     * decision (toolUseId for tools, uuid for text), and this class does not need to know it.
     */
    appendChild(
        parentToolUseId: string,
        entry: UiEntry,
        upsertKey: (e: UiEntry) => string,
    ): boolean {
        const parent = this.findTool(parentToolUseId);
        if (!parent) {
            return false;
        }
        const parentPath = this._index.get(parent.id);
        const ok = this.update<UiToolEntry>(parent.id, (p) => {
            const kids = p.children ?? { items: [], hasMore: false, showAll: false };
            if (kids.showAll) {
                return { ...p, children: { ...kids, items: upsert(kids.items, entry, upsertKey) } };
            }
            return {
                ...p,
                children: {
                    ...kids,
                    hasMore: kids.hasMore || kids.items.length >= 3,
                    items: [...kids.items, entry].slice(-3),
                },
            };
        });
        if (ok) {
            this._index.set(entry.id, [...(parentPath ?? []), parentToolUseId]);
        }
        return ok;
    }

    /** Swap the whole transcript (a history page load). The index is rebuilt from scratch: one
     *  that outlives its entries would resolve an id to a path that leads nowhere. */
    replaceAll(entries: UiEntry[]): void {
        // Deliberately does NOT fire onCleared, unlike clear(). The caller builds the replacement
        // entries before handing them over, and building them is what refills the id-keyed maps for
        // the replayed turns — an invalidation at this point would wipe what the replay had just
        // written. The history listener drops the old ids before it starts replaying, which is the
        // only moment where "these ids are dead" and "these ids are being written" don't overlap.
        this._entries = entries;
        this._reindex();
    }

    /** Put an older page in front, keeping the rest as-is. */
    prepend(older: UiEntry[]): void {
        this._entries = [...older, ...this._entries];
        this._reindex();
    }

    /** Update several entries at once — the streaming messages a turn ends with. */
    updateMany(ids: number[], fn: (e: UiEntry) => UiEntry): void {
        for (const id of ids) {
            this.update(id, fn);
        }
    }

    /** Find a tool row by toolUseId, walking nested children. */
    findTool(toolUseId: string): UiToolEntry | null {
        return this._visitTools((e) => e.toolUseId === toolUseId);
    }

    /** Locate an Agent row by the sub-agent it spawned. Unique: only an Agent row carries an
     *  agentId, and it names the transcript that row alone opened. */
    findToolByAgentId(agentId: string): UiToolEntry | null {
        return this._visitTools((e) => e.agentId === agentId);
    }

    /** Rebuild id → path for the whole tree, children included. History hands a page over with
     *  its children already nested, where the live path builds them one appendChild at a time. */
    private _reindex(): void {
        this._index.clear();
        const walk = (list: readonly UiEntry[], path: EntryPath): void => {
            for (const e of list) {
                this._index.set(e.id, path);
                if (e.kind === 'tool' && e.children?.items.length) {
                    walk(e.children.items, [...path, e.toolUseId]);
                }
            }
        };
        walk(this._entries, []);
    }

    /** First tool row matching `pred`, depth-first. */
    private _visitTools(pred: (e: UiToolEntry) => boolean): UiToolEntry | null {
        const visit = (list: readonly UiEntry[]): UiToolEntry | null => {
            for (const e of list) {
                if (e.kind !== 'tool') {
                    continue;
                }
                if (pred(e)) {
                    return e;
                }
                if (e.children?.items.length) {
                    const hit = visit(e.children.items);
                    if (hit) {
                        return hit;
                    }
                }
            }
            return null;
        };
        return visit(this._entries);
    }

    /** Rebuild `list` with the entry at `path[depth…]`/`id` replaced. Returns null when the path
     *  no longer resolves, so a stale index cannot corrupt the tree. */
    private _replace(
        list: UiEntry[],
        path: EntryPath,
        depth: number,
        id: number,
        fn: (e: UiEntry) => UiEntry,
    ): UiEntry[] | null {
        if (depth === path.length) {
            const i = list.findIndex((e) => e.id === id);
            if (i < 0) {
                return null;
            }
            const next = [...list];
            next[i] = fn(list[i]);
            return next;
        }
        const toolUseId = path[depth];
        const i = list.findIndex((e) => e.kind === 'tool' && e.toolUseId === toolUseId);
        const parent = i < 0 ? null : list[i];
        if (parent?.kind !== 'tool' || !parent.children) {
            return null;
        }
        const items = this._replace(parent.children.items, path, depth + 1, id, fn);
        if (!items) {
            return null;
        }
        const next = [...list];
        next[i] = { ...parent, children: { ...parent.children, items } };
        return next;
    }

    /** Resolve an indexed path to the live entry, or null when the tree no longer holds it. */
    private _walk(path: EntryPath, id: number): UiEntry | null {
        let list: readonly UiEntry[] = this._entries;
        for (const toolUseId of path) {
            const parent = list.find((e) => e.kind === 'tool' && e.toolUseId === toolUseId);
            if (parent?.kind !== 'tool' || !parent.children) {
                return null;
            }
            list = parent.children.items;
        }
        return list.find((e) => e.id === id) ?? null;
    }
}

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
 * that is what the previous `_commit` left to each caller to remember, and got wrong.
 */
export class Transcript {
    private _entries: UiEntry[] = [];
    /** id → path, so find/update are O(1) instead of walking the tree on every event. */
    private _index = new Map<number, EntryPath>();

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
        const next = this._replace(this._entries, path, 0, id, (e) => fn(e as T));
        if (!next) {
            return false;
        }
        this._entries = next;
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

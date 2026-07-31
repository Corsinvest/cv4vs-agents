// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import type { UiEntry } from './types';

/** Chain of toolUseId from the root down to a nested entry; empty for a top-level one. */
type EntryPath = string[];

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

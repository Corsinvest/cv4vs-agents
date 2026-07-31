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

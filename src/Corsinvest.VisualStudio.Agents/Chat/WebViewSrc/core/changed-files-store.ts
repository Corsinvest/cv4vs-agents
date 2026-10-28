/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import type { ChangedFileDto } from './generated/ChangedFileDto';
import type { ChangedFilesNotification } from './generated/ChangedFilesNotification';

type Listener = () => void;

/**
 * The Changes list as the WebView holds it: the last list the host sent, in the order it sent it
 * (sorted by path, host side), and nothing derived from anything else. The host is the authority:
 * it pushes a new list whenever a row or a flag moves, including an empty one when the session
 * changes, so there is no clearing to keep in step with the chat's own.
 *
 * Pure, with no DOM: the chip reads it, the tests drive it.
 */
export class ChangedFilesStore {
    private _files: readonly ChangedFileDto[] = [];
    private _overflow = false;
    private _maxFiles = 0;
    private _disabled: string | null = null;
    private readonly _listeners = new Set<Listener>();

    get files(): readonly ChangedFileDto[] {
        return this._files;
    }

    /** More files changed than are listed. */
    get overflow(): boolean {
        return this._overflow;
    }

    get maxFiles(): number {
        return this._maxFiles;
    }

    /** Why nothing is tracked (file checkpoints off), or null while something is. */
    get disabled(): string | null {
        return this._disabled;
    }

    apply(list: ChangedFilesNotification): void {
        this._files = list.files;
        this._overflow = list.overflow;
        this._maxFiles = list.maxFiles;
        this._disabled = list.disabled;
        this._notify();
    }

    clear(): void {
        this._files = [];
        this._overflow = false;
        this._disabled = null;
        this._notify();
    }

    /** Called after every change. Returns the unsubscribe. */
    subscribe(listener: Listener): () => void {
        this._listeners.add(listener);
        return () => {
            this._listeners.delete(listener);
        };
    }

    private _notify(): void {
        for (const listener of [...this._listeners]) {
            listener();
        }
    }
}

/** The one list of this page. */
export const changedFiles = new ChangedFilesStore();

/** A path as the list shows it: the file's name, and the folder it is in. */
export function splitShownPath(shown: string): { name: string; dir: string } {
    const cut = Math.max(shown.lastIndexOf('/'), shown.lastIndexOf('\\'));
    return cut < 0
        ? { name: shown, dir: '' }
        : { name: shown.slice(cut + 1), dir: shown.slice(0, cut + 1) };
}

/** One letter for the state, as a version-control list gives it. */
export function stateLetter(state: string): string {
    switch (state) {
        case 'added':
            return 'A';
        case 'deleted':
            return 'D';
        default:
            return 'M';
    }
}

/** What to say in place of the numbers when they cannot be shown as plain figures. */
export function countsNote(file: Pick<ChangedFileDto, 'counts' | 'isBinary'>): string | null {
    if (file.isBinary) {
        return 'binary';
    }
    return file.counts === 'unavailable' ? 'not counted' : null;
}

/** Why a file cannot be undone, or null when it can. */
export function undoBlocker(
    file: Pick<ChangedFileDto, 'hasBaseline' | 'baselineMissing'>,
): string | null {
    if (file.hasBaseline) {
        return null;
    }
    return file.baselineMissing
        ? 'The copy of this file from before Claude changed it is gone.'
        : 'There is no copy of this file from before Claude changed it.';
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

import type { Attachment } from './types';

/**
 * Prompts written to the CLI while a turn was running, and not read yet.
 *
 * The queue is the CLI's, not ours: it decides when a prompt is read and whether several become
 * one message. This only remembers what was sent, so that the replay the CLI sends back for each
 * (--replay-user-messages, same uuid) can be told for what it is.
 *
 * Measured on CLI 2.1.291: at a tool boundary every pending prompt comes back alone, one replay
 * each. At the end of a turn they come back alone except the LAST, whose replay carries the
 * blocks of all of them: that one message is what the model got, and what the .jsonl keeps.
 */

/** A prompt as the composer sent it. Kept whole: taking it back restores it into the composer. */
export interface PendingPrompt {
    uuid: string;
    text: string;
    attachments: Attachment[];
    /** Sent with `priority: 'later'`: it waits for the end of the turn rather than the next tool. */
    later: boolean;
}

/** What a replay holds, reduced to what tells a prompt from a merge of several. */
export interface ReplayShape {
    text: string;
    images: number;
    files: number;
}

export type ReplayAction =
    /** Not a prompt sent mid-turn from here. */
    | { kind: 'ignore' }
    /** Read as it was sent: the bubble moves to where the replay arrived. */
    | { kind: 'read'; uuid: string }
    /** This replay is the merge: one bubble under `uuid`, the `absorbed` ones go. */
    | { kind: 'merged'; uuid: string; absorbed: string[] };

const norm = (s: string): string => s.replace(/\s+/g, ' ').trim();

export class PendingPrompts {
    private _pending = new Map<string, PendingPrompt>();
    /** Read since the model last said anything: the only ones a merge can still absorb. */
    private _batch: PendingPrompt[] = [];
    /** Being taken back: asked of the CLI, not answered yet. Kept apart from `_pending` because
     *  the CLI says "cancelled" on its lifecycle line a moment BEFORE it answers the request
     *  (measured), and that line removes the prompt: the copy to give back must outlive it. */
    private _takingBack = new Map<string, PendingPrompt>();
    private _turnRunning = false;

    add(p: PendingPrompt): void {
        this._pending.set(p.uuid, p);
    }

    has(uuid: string): boolean {
        return this._pending.has(uuid);
    }

    get(uuid: string): PendingPrompt | undefined {
        return this._pending.get(uuid);
    }

    /** Still waiting, in the order they were sent. */
    get uuids(): string[] {
        return [...this._pending.keys()];
    }

    onReplay(uuid: string, replay: ReplayShape): ReplayAction {
        const own = this._pending.get(uuid);
        if (!own) {
            return { kind: 'ignore' };
        }
        this._pending.delete(uuid);
        const earlier = this._batch;
        if (this._isMerge(own, earlier, replay)) {
            this._batch = [own];
            return { kind: 'merged', uuid, absorbed: earlier.map((e) => e.uuid) };
        }
        this._batch = [...earlier, own];
        return { kind: 'read', uuid };
    }

    /** The model produced something, or the turn ended: whatever was read before is settled. */
    onTurnProgress(): void {
        this._batch = [];
    }

    /** Cancelled or dropped: no longer pending. Returns them in the order they were sent. */
    remove(uuids: readonly string[]): PendingPrompt[] {
        const drop = new Set(uuids);
        const gone = [...this._pending.values()].filter((p) => drop.has(p.uuid));
        for (const p of gone) {
            this._pending.delete(p.uuid);
        }
        return gone;
    }

    clear(): void {
        this._pending.clear();
        this._batch = [];
        this._takingBack.clear();
        this._turnRunning = false;
    }

    /** The CLI process is gone: nothing pending will ever be read. Hands back what was waiting,
     *  in the order it was sent, so it can be returned to the composer rather than lost. */
    drain(): PendingPrompt[] {
        const all = [...this._pending.values()];
        this.clear();
        return all;
    }

    /** Ask to take a prompt back. False when it is not pending, or already being asked for: a
     *  second click on the cross must not send a second request. */
    beginTakeBack(uuid: string): boolean {
        const p = this._pending.get(uuid);
        if (!p || this._takingBack.has(uuid)) {
            return false;
        }
        this._takingBack.set(uuid, p);
        return true;
    }

    /** The CLI's answer. Cancelled: the prompt is ours again and is returned. Not cancelled: it
     *  had been read, stays pending until its replay, and nothing is returned. */
    endTakeBack(uuid: string, cancelled: boolean): PendingPrompt | undefined {
        const p = this._takingBack.get(uuid);
        this._takingBack.delete(uuid);
        if (!p || !cancelled) {
            return undefined;
        }
        this._pending.delete(uuid);
        return p;
    }

    turnStarted(): void {
        this._turnRunning = true;
    }

    turnEnded(): void {
        this._turnRunning = false;
    }

    /** A turn is running, or one is about to: the CLI starts a turn by itself for whatever is
     *  still pending when one ends. Recomputed whenever a prompt stops being pending, or the last
     *  one to go with no turn running would leave the composer reading as busy for good. */
    get busy(): boolean {
        return this._turnRunning || this._pending.size > 0;
    }

    /**
     * A merge holds the earlier prompts' texts as well as its own. Containing them is not enough:
     * "ok do it" contains "ok". It must also be long enough to hold them all, or carry more
     * attachments than the prompt had alone.
     *
     * A slash command never merges: its replay is the command's envelope, unlike anything typed.
     */
    private _isMerge(own: PendingPrompt, earlier: PendingPrompt[], replay: ReplayShape): boolean {
        if (earlier.length === 0 || own.text.trimStart().startsWith('/')) {
            return false;
        }
        const text = norm(replay.text);
        if (!earlier.every((e) => text.includes(norm(e.text)))) {
            return false;
        }
        const ownAttachments = own.attachments.length;
        const earlierAttachments = earlier.reduce((n, e) => n + e.attachments.length, 0);
        const replayAttachments = replay.images + replay.files;
        const textsLength = earlier.reduce(
            (n, e) => n + norm(e.text).length,
            norm(own.text).length,
        );
        return earlierAttachments > 0
            ? replayAttachments > ownAttachments
            : text.length >= textsLength && text !== norm(own.text);
    }
}

/** The one instance: the composer adds to it, the transcript reads the replays against it. */
export const pendingPrompts = new PendingPrompts();

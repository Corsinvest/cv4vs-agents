/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/**
 * Prompts sent to the CLI whose replay (--replay-user-messages) has not come back yet.
 *
 * Asked of this list, not of the transcript: /clear empties the transcript between the send and
 * the replay, and the menu's "Clear conversation" never echoes a bubble at all — a transcript
 * lookup would take either replay for a prompt typed elsewhere and show it.
 */
const awaitingReplay = new Set<string>();

/** A prompt left for the CLI: its replay, when it comes back, is ours. */
export function markSent(uuid: string): void {
    awaitingReplay.add(uuid);
}

/** True, once, for the replay of a prompt we sent: the entry goes with it. */
export function takeReplay(uuid: string): boolean {
    return awaitingReplay.delete(uuid);
}

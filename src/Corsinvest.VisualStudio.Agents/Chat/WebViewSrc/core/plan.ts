/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/**
 * Whether an ExitPlanMode call carries anything to open in the editor.
 *
 * The CLI injects `plan` and `planFilePath` from the plan file it expects the model to have
 * written, and injects nothing when there is no such file — which is what happens when the model
 * leaves plan mode without writing one. An "Open in editor" button there only leads to an error
 * notice, so the button follows this instead.
 */
export function hasPlanToOpen(input: Record<string, unknown> | null | undefined): boolean {
    const nonEmpty = (v: unknown): boolean => typeof v === 'string' && v.length > 0;
    return nonEmpty(input?.plan) || nonEmpty(input?.planFilePath);
}

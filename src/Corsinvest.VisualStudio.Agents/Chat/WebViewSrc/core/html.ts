/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// HTML-string helpers for the UI layer: pure functions returning strings
// (not DOM nodes) for template literals and data-* attribute payloads.

/**
 * HTML-entity-encode the input. Returns '' for null/undefined.
 */
export function escapeHtml(s: unknown): string {
    return String(s ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

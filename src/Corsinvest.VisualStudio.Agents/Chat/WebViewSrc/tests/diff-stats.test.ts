// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { countChanges } from '../core/diff-stats.ts';

test('countChanges: sostituzione 14 righe con 1 conta +1 -14, non il netto', () => {
    const oldStr = Array.from({ length: 14 }, (_, i) => `riga ${i}`).join('\n');
    const newStr = 'una sola';

    assert.deepEqual(countChanges(oldStr, newStr, 'f.ts', false), { added: 1, removed: 14 });
});

test('countChanges: solo aggiunte', () => {
    // Trailing newline on oldStr: without it jsdiff treats the shared first line as
    // changed too (no-newline-at-EOF is part of the line, same as `git diff` does).
    assert.deepEqual(countChanges('a\n', 'a\nb\nc', 'f.ts', false), { added: 2, removed: 0 });
});

test('countChanges: nessuna modifica', () => {
    assert.deepEqual(countChanges('a\nb', 'a\nb', 'f.ts', false), { added: 0, removed: 0 });
});

test('countChanges: file vuoto verso contenuto (Write)', () => {
    assert.deepEqual(countChanges('', 'x\ny', 'f.ts', false), { added: 2, removed: 0 });
});

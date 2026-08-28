// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { countChanges } from '../core/diff-stats.ts';

test('countChanges: replacing 14 lines with 1 counts +1 -14, not the net', () => {
    const oldStr = Array.from({ length: 14 }, (_, i) => `line ${i}`).join('\n');
    const newStr = 'just one';

    assert.deepEqual(countChanges(oldStr, newStr, 'f.ts', false), { added: 1, removed: 14 });
});

test('countChanges: additions only', () => {
    // Trailing newline on oldStr: without it jsdiff treats the shared first line as
    // changed too (no-newline-at-EOF is part of the line, same as `git diff` does).
    assert.deepEqual(countChanges('a\n', 'a\nb\nc', 'f.ts', false), { added: 2, removed: 0 });
});

test('countChanges: no change', () => {
    assert.deepEqual(countChanges('a\nb', 'a\nb', 'f.ts', false), { added: 0, removed: 0 });
});

test('countChanges: empty file to content (Write)', () => {
    assert.deepEqual(countChanges('', 'x\ny', 'f.ts', false), { added: 2, removed: 0 });
});

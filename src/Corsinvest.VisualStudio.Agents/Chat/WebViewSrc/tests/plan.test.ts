// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { hasPlanToOpen } from '../core/plan.ts';

test('hasPlanToOpen: a plan file or plan text is something to open', () => {
    assert.equal(hasPlanToOpen({ planFilePath: 'C:\\plans\\p.md' }), true);
    assert.equal(hasPlanToOpen({ plan: '# Plan' }), true);
    assert.equal(hasPlanToOpen({ plan: '# Plan', planFilePath: 'C:\\plans\\p.md' }), true);
});

test('hasPlanToOpen: nothing injected means nothing to open', () => {
    // The model left plan mode without writing a plan file: the CLI sends an empty input.
    assert.equal(hasPlanToOpen({}), false);
    assert.equal(hasPlanToOpen(undefined), false);
    assert.equal(hasPlanToOpen(null), false);
});

test('hasPlanToOpen: empty or non-string values do not count', () => {
    assert.equal(hasPlanToOpen({ plan: '', planFilePath: '' }), false);
    assert.equal(hasPlanToOpen({ plan: 42, planFilePath: true }), false);
});

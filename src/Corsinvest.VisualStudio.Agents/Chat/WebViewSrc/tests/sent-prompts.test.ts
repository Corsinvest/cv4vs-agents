// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { markSent, takeReplay } from '../core/sent-prompts.ts';

test('the replay of a prompt we sent is taken, once', () => {
    markSent('sent-here');

    assert.equal(takeReplay('sent-here'), true);
    assert.equal(takeReplay('sent-here'), false, 'the entry went with the first replay');
});

test('a prompt typed elsewhere was never sent from here', () => {
    assert.equal(takeReplay('typed-on-the-web'), false);
});

test('the echo, which comes before the send, is not taken for a replay', () => {
    assert.equal(takeReplay('echoed-then-sent'), false);
    markSent('echoed-then-sent');
    assert.equal(takeReplay('echoed-then-sent'), true);
});

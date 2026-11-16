// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 Corsinvest Srl

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { symbolIconKey } from '../core/symbol-kind.ts';
import { symbolIconUrl } from '../core/icon-url.ts';

// Kinds come from NavigateTo and the navigation bar's glyphs, in whatever case they arrive. The key
// names the Visual Studio image the host renders (IconCacheService), the one Solution Explorer uses.
test('symbolIconKey: each main kind has its own image, case-insensitive', () => {
    assert.equal(symbolIconKey('Class'), 'symbol-class');
    assert.equal(symbolIconKey('method'), 'symbol-method');
    assert.equal(symbolIconKey('ENUM'), 'symbol-enum');
    assert.equal(symbolIconKey('Interface'), 'symbol-interface');
    assert.equal(symbolIconKey('Structure'), 'symbol-struct');
    assert.equal(symbolIconKey('Struct'), 'symbol-struct');
    assert.equal(symbolIconKey('Property'), 'symbol-property');
    assert.equal(symbolIconKey('Field'), 'symbol-field');
    assert.equal(symbolIconKey('Event'), 'symbol-event');
    assert.equal(symbolIconKey('Namespace'), 'symbol-namespace');
    assert.equal(symbolIconKey('Delegate'), 'symbol-delegate');
    assert.equal(symbolIconKey('Module'), 'symbol-module');
    assert.equal(symbolIconKey('Constant'), 'symbol-constant');
    assert.equal(symbolIconKey('Operator'), 'symbol-operator');
});

test('symbolIconKey: different names for the same thing share an image', () => {
    // The navigation bar says EnumMember, NavigateToItemKind says EnumItem.
    assert.equal(symbolIconKey('EnumMember'), 'symbol-enummember');
    assert.equal(symbolIconKey('EnumItem'), 'symbol-enummember');
    assert.equal(symbolIconKey('Constructor'), 'symbol-method');
    assert.equal(symbolIconKey('Record'), 'symbol-class');
});

test('symbolIconKey: unknown or missing kind falls back', () => {
    assert.equal(symbolIconKey(null), 'symbol-other');
    assert.equal(symbolIconKey(undefined), 'symbol-other');
    assert.equal(symbolIconKey(''), 'symbol-other');
    assert.equal(symbolIconKey('Unknown'), 'symbol-other');
});

test('symbolIconUrl: served from the icon host like file icons', () => {
    assert.equal(symbolIconUrl('Method'), 'https://cv4vs-icons.invalid/symbol-method.png');
});

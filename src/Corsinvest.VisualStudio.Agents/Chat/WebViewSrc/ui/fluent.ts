/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Fluent UI Web Components v3 bootstrap.
// v3 registers via side-effect imports per tag and themes via `setTheme()`.
// Only the components used in the WebView are imported (esbuild tree-shaking).

import '@fluentui/web-components/button.js';
import '@fluentui/web-components/counter-badge.js';
import '@fluentui/web-components/dialog.js';
import '@fluentui/web-components/dialog-body.js';
import '@fluentui/web-components/link.js';
import '@fluentui/web-components/listbox.js';
import '@fluentui/web-components/message-bar.js';
import '@fluentui/web-components/menu.js';
import '@fluentui/web-components/menu-item.js';
import '@fluentui/web-components/dropdown.js';
import '@fluentui/web-components/menu-list.js';
import '@fluentui/web-components/option.js';
import '@fluentui/web-components/progress-bar.js';
import '@fluentui/web-components/checkbox.js';
import '@fluentui/web-components/radio.js';
import '@fluentui/web-components/switch.js';
import '@fluentui/web-components/tab.js';
import '@fluentui/web-components/tablist.js';
import '@fluentui/web-components/text-input.js';
import '@fluentui/web-components/textarea.js';
import '@fluentui/web-components/tooltip.js';
import { setTheme } from '@fluentui/web-components';
import { webDarkTheme, webLightTheme } from '@fluentui/tokens';

let _initialized = false;

/** No-op kept for symmetry with the old v2 API; registration is via the imports above. */
export function initFluent(): void {
    if (_initialized) {
        return;
    }
    _initialized = true;
}

/** Swap the active Fluent theme; drives every `<fluent-*>` element via `:root` CSS vars. */
export function applyFluentTheme(isDark: boolean): void {
    setTheme(isDark ? webDarkTheme : webLightTheme);
}

// Fluent's default type ramp (px). The base step (Base300) is the document base;
// the user's chat-font-size scales the whole ramp off it so everything — chat,
// composer, fluent-* controls — grows together.
const FONT_RAMP: Record<string, { size: number; line: number }> = {
    100: { size: 10, line: 14 },
    200: { size: 12, line: 16 },
    300: { size: 14, line: 20 },
    400: { size: 16, line: 22 },
    500: { size: 20, line: 26 },
    600: { size: 24, line: 32 },
};
const FONT_BASE = 14; // Base300 px
const FONT_MIN = 9; // below this the UI is unreadable
const FONT_MAX = 28; // above this the chat is unusable in a tool window

/**
 * Scale the whole Fluent type ramp by the user's chat font size. setTheme()
 * writes the tokens into an adoptedStyleSheet on `html`; an inline style on
 * documentElement outranks it and survives later setTheme() calls, so we only
 * re-run this when the option changes — not on every theme swap.
 *
 * The size is clamped (and NaN/≤0 falls back to the base) so a bad option value
 * can't shrink the UI to nothing or blow it up.
 */
export function applyFontScale(chatFontSizePx: number): void {
    const px =
        chatFontSizePx > 0 ? Math.min(Math.max(chatFontSizePx, FONT_MIN), FONT_MAX) : FONT_BASE;
    const scale = px / FONT_BASE;
    const root = document.documentElement.style;
    for (const [step, { size, line }] of Object.entries(FONT_RAMP)) {
        root.setProperty(`--fontSizeBase${step}`, `${(size * scale).toFixed(2)}px`);
        root.setProperty(`--lineHeightBase${step}`, `${(line * scale).toFixed(2)}px`);
    }
}

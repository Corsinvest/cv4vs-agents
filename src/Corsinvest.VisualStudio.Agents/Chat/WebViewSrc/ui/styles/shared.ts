/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { css } from 'lit';

/**
 * Shared static styles for Shadow-DOM components. The global `fill: currentColor` rule in
 * base.css (which fixes the @fluentui/svg-icons that ship without a fill) does NOT cross the
 * shadow boundary, so components that render inline SVG icons include this in their
 * `static styles` array: `static styles = [iconStyles, css`...`]`.
 */
export const iconStyles = css`
    svg {
        fill: currentColor;
    }
    /* Outline icons declare fill="none" on the root <svg>; respect it. */
    svg[fill='none'] {
        fill: none;
    }
`;

/**
 * Shared shell for the info dialogs (Account & Usage, Context usage, Statistics), all built
 * on fluent-dialog + fluent-dialog-body. Tall content overflows Fluent's default
 * max-height:100vh; cap the box at 85vh and let the body content part scroll inside it (the
 * title/close row stays pinned). min-height:0 lets the content row shrink so scroll engages.
 * Include in each dialog's `static styles`: `static styles = [dialogStyles, css`...`]`.
 */
export const dialogStyles = css`
    :host {
        display: contents;
    }
    .cv-dialog-loading {
        padding: 12px 0;
        color: var(--colorNeutralForeground3);
    }
    fluent-dialog::part(dialog) {
        max-height: 85vh;
    }
    fluent-dialog-body {
        min-height: 0;
    }
    fluent-dialog-body::part(content) {
        min-height: 0;
        overflow-y: auto;
    }
`;

/**
 * Shared status dot: a small filled circle used to flag activity/state. `.cv-dot` is the neutral
 * base; `.cv-dot.active` blinks in the brand colour (running, like the chat tool rows);
 * `.cv-dot.done` is a steady green. Include in a component's `static styles` and set the size via
 * `--cv-dot-size` (default 8px). Mirrors the chat `dotBlink` (chat.css) so live and shadow-DOM
 * components share one animation.
 */
export const statusDotStyles = css`
    .cv-dot {
        flex-shrink: 0;
        width: var(--cv-dot-size, 8px);
        height: var(--cv-dot-size, 8px);
        border-radius: 50%;
        background: currentColor;
        color: var(--colorNeutralForeground3);
    }
    .cv-dot.active {
        color: var(--colorBrandBackground);
        animation: cvDotBlink 1s linear infinite;
    }
    .cv-dot.done {
        color: var(--colorPaletteGreenForeground1);
    }
    @keyframes cvDotBlink {
        0%,
        100% {
            opacity: 1;
        }
        50% {
            opacity: 0;
        }
    }
`;

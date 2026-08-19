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
    /* Fluent's small icon-only button scales its glyph to 20px; pin it to 14, the size every
     * action icon in the chat is drawn at. Only icon-only buttons — labelled ones keep theirs. */
    fluent-button[icon-only] svg {
        width: 14px;
        height: 14px;
    }
`;

/** Icon-only `<fluent-button>` cut to the app's density — even `size="small"` is padded for a
 *  control standing on its own. Layout only, so the rest still comes from Fluent. */
export const iconTriggerStyles = css`
    .trigger {
        padding: 3px;
        min-width: 0;
    }
`;

/**
 * Shared tooltip for the composer's triggers. A `title` attribute is drawn by the OS, so it keeps
 * the system's light/dark whatever theme VS is in — `fluent-tooltip` is themed like the rest.
 *
 * Nearly every tooltip here is one bare line: a trigger showing an icon needs naming, one showing a
 * value needs only the part it had to shorten, and a trigger that opens a list needs neither —
 * the list carries a description per row already. Never a line for what clicking does, either;
 * that is what the trigger being a button says.
 *
 * `.tip-name` / `.tip-desc` are what is left, for the one case with two things to say at once.
 */
export const tooltipStyles = css`
    /* :popover-open, not the bare tag: fluent-tooltip is a popover, and a closed popover is hidden
       by a UA display:none that a plain display:flex here would override — which left the tooltip
       on screen permanently. Layout only applies once it opens. */
    fluent-tooltip:popover-open {
        display: flex;
        flex-direction: column;
        gap: 2px;
    }
    fluent-tooltip {
        padding: 6px 8px;
        max-width: 320px;
        box-sizing: border-box;
        /* Lets a plain string with newlines break where it says to, so a tooltip with two or three
           lines doesn't need an element per line. */
        white-space: pre-line;
    }
    .tip-name {
        font-weight: var(--fontWeightSemibold);
        /* A file path has no spaces to break at, so without this one long path sets the tooltip's
           width and max-width can only clip it. */
        overflow-wrap: anywhere;
    }
    .tip-desc {
        color: var(--colorNeutralForeground2);
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

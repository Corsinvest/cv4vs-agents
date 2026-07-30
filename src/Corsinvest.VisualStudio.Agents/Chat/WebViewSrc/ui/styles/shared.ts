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
    /* Fluent's small icon-only button scales its glyph to 20px; pin it to the nominal
     * 16px so the action icons (copy, chevron, toggles) read as one size in the dense
     * chat and don't look oversized. Only icon-only buttons — labelled ones keep theirs. */
    fluent-button[icon-only] svg {
        width: 16px;
        height: 16px;
    }
`;

/**
 * Shared icon-action button — the Shadow-DOM twin of `.icon-btn` in chat.css, which the light DOM
 * uses for the same thing. A bare `<button>`, not a fluent-button: Fluent sizes a standalone
 * control, and cutting it down to an icon's own size means overriding its tokens, which the
 * fluent-pure rule forbids. Owning the element instead keeps that rule intact.
 *
 * Glyph size comes from `--cv-icon-btn-size` (default 14px, the density of an action inside the
 * transcript). A composer control sets 16px: it is permanent chrome, sits beside a text label, and
 * has to be worth aiming at.
 */
export const iconButtonStyles = css`
    .icon-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        padding: 3px;
        border: 0;
        border-radius: var(--borderRadiusSmall);
        background: transparent;
        /* Foreground2, not the inherited Foreground1: these sit beside the message they act on and
           should not compete with it. Full strength on hover, below. */
        color: var(--colorNeutralForeground2);
        cursor: pointer;
        opacity: 0.75;
    }
    .icon-btn:hover,
    .icon-btn:focus-visible {
        color: var(--colorNeutralForeground1);
    }
    /* NOT --colorSubtleBackgroundHover: in the light theme that token is #f5f5f5, three points of
       luminance off the #ffffff behind it — the hover is there and invisible. Mixing the foreground
       inverts with the theme by construction, so the tint reads on either. Same as the IDE-context
       chip beside these buttons. */
    .icon-btn:hover {
        background: color-mix(in srgb, var(--colorNeutralForeground1) 10%, transparent);
        opacity: 1;
    }
    .icon-btn:active {
        background: color-mix(in srgb, var(--colorNeutralForeground1) 16%, transparent);
    }
    .icon-btn:focus-visible {
        outline: 1px solid var(--colorStrokeFocus2, currentColor);
        outline-offset: 1px;
    }
    .icon-btn svg {
        width: var(--cv-icon-btn-size, 14px);
        height: var(--cv-icon-btn-size, 14px);
        display: block;
        fill: currentColor;
    }
    /* The label of a button that carries one (the composer's model/effort/permission triggers).
       Base200: below the body text, since these name a setting rather than say anything, but above
       the Base100 of the IDE-context chip — on those three the word is the whole control, with no
       glyph to carry it. nowrap so the button never wraps onto a second row. */
    .icon-btn span {
        font-size: var(--fontSizeBase200);
        white-space: nowrap;
    }
    /* A labelled button reads as a pill: a word on its own needs more room than a glyph (3px is
       right for a 14px icon, cramped for text), and a faint fill with a full radius says "this is
       one control" where three bare words in a row would read as one phrase. Enough on its own
       that no separator is needed between them. */
    .icon-btn:has(span) {
        padding: 2px 8px;
        border-radius: 999px;
        border: 1px solid var(--colorNeutralStroke2);
        background: var(--colorNeutralBackground1);
        opacity: 1;
    }
    .icon-btn:has(span):hover {
        border-color: var(--colorNeutralStroke1);
        background: color-mix(
            in srgb,
            var(--colorNeutralForeground1) 8%,
            var(--colorNeutralBackground1)
        );
    }
    .icon-btn svg + span {
        margin-left: 4px;
    }
`;

/**
 * Shared tooltip for the composer's triggers. A `title` attribute is drawn by the OS, so it keeps
 * the system's light/dark whatever theme VS is in — `fluent-tooltip` is themed like the rest.
 *
 * Three optional lines, in the order the reader needs them: what the setting is on (`.tip-name`),
 * what that means (`.tip-desc`), and what clicking does (`.tip-action`).
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
    }
    .tip-name {
        font-weight: var(--fontWeightSemibold);
    }
    .tip-desc {
        color: var(--colorNeutralForeground2);
    }
    /* The verb, set apart from the description of what is currently in force. */
    .tip-action {
        color: var(--colorNeutralForeground3);
        font-size: var(--fontSizeBase100);
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

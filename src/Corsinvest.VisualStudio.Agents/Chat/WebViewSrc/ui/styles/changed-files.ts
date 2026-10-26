/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { css } from 'lit';

/**
 * The Changes chip's styles. The chip sits inside the prompt's shadow root, and the global sheet
 * (bundle.css) does not reach into a shadow root: a rule written there is simply not applied, and
 * the chip comes out as unstyled buttons laid over the toolbar. So everything it draws is here:
 * its own layout, the icon button and the four glyphs it uses (base.css has the first for the
 * light-DOM rows of the transcript, the same values), and the colours of the line counts.
 * Include with `iconStyles` and `iconTriggerStyles`.
 */
export const changedFilesStyles = css`
    :host {
        position: relative;
        display: inline-flex;
    }

    /* Tracking is off (file checkpoints): the chip says so and nothing is listed under it. */
    .trigger.off {
        opacity: 0.55;
    }

    /* Wrap the icon so the count can overlay its top-right corner, notification style. It is also
     * the laid-out box the tooltip anchors to. */
    .cv-changes-anchor {
        position: relative;
        display: inline-flex;
    }
    .cv-changes-count {
        position: absolute;
        top: -7px;
        right: -9px;
    }

    /* Click panel: absolutely positioned above the chip. Plain absolute rather than CSS anchor
     * positioning, which the VS WebView2's Chromium does not honour reliably. The chip is not at
     * the left edge of a narrow chat, so the panel's left and width are set from the chip's place
     * when it opens (CvChangedFilesChip._place) to keep its right edge on screen. */
    .cv-changes-popover {
        position: absolute;
        bottom: calc(100% + 4px);
        left: 0;
        z-index: 1000;
        box-sizing: border-box;
        width: 540px;
        max-width: calc(100vw - 16px);
        padding: 8px 10px;
        font-size: var(--fontSizeBase300);
        background: var(--colorNeutralBackground1);
        color: var(--colorNeutralForeground1);
        border: 1px solid var(--colorNeutralStroke1);
        border-radius: var(--borderRadiusMedium);
        box-shadow: var(--shadow16);
    }
    .cv-changes-popover[hidden] {
        display: none;
    }

    .cv-changes-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        margin-bottom: 8px;
        font-weight: var(--fontWeightSemibold);
    }
    .cv-changes-bulk {
        display: inline-flex;
        align-items: center;
        gap: 4px;
        font-weight: var(--fontWeightRegular);
    }

    .cv-changes-list {
        max-height: min(50vh, 420px);
        overflow-y: auto;
    }
    .cv-changes-row {
        display: flex;
        align-items: center;
        gap: 6px;
        padding: 2px 0;
        border-bottom: 1px solid var(--colorNeutralStroke3);
    }

    /* The file: its icon, its name, and the folder it is in. A button, because a click on it opens the
     * change; the dedicated button at the end says the same for whoever looks for it there. */
    .cv-changes-file {
        flex: 1;
        min-width: 0;
        display: flex;
        align-items: center;
        gap: 6px;
        margin: 0;
        padding: 2px 4px;
        border: 0;
        border-radius: var(--borderRadiusMedium);
        background: transparent;
        color: inherit;
        font: inherit;
        text-align: start;
        cursor: pointer;
    }
    .cv-changes-file:hover {
        background-color: var(--colorSubtleBackgroundHover);
    }
    .cv-changes-file:focus-visible {
        outline: 2px solid var(--colorStrokeFocus2);
    }
    .cv-changes-fileicon {
        width: 16px;
        height: 16px;
        flex-shrink: 0;
    }
    .cv-changes-name {
        flex-shrink: 0;
        max-width: 60%;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
    }
    .cv-changes-dir {
        min-width: 0;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
        font-size: var(--fontSizeBase200);
        color: var(--colorNeutralForeground3);
    }

    /* Where the warning goes, whether or not there is one. */
    .cv-changes-slot {
        flex-shrink: 0;
        display: inline-flex;
        width: 14px;
        height: 14px;
    }
    .cv-changes-state {
        flex-shrink: 0;
        width: 1.4em;
        text-align: center;
        font-size: var(--fontSizeBase200);
        font-weight: var(--fontWeightSemibold);
        color: var(--colorNeutralForeground2);
    }
    .cv-changes-state.added {
        color: var(--colorPaletteGreenForeground1);
    }
    .cv-changes-state.deleted {
        color: var(--colorPaletteRedForeground1);
    }
    .cv-changes-counts {
        flex-shrink: 0;
        min-width: 7em;
        text-align: end;
        font-size: var(--fontSizeBase200);
        font-variant-numeric: tabular-nums;
    }
    .cv-changes-quiet {
        color: var(--colorNeutralForeground3);
    }
    .cv-diff-count-ins {
        color: var(--colorPaletteGreenForeground1);
    }
    .cv-diff-count-del {
        color: var(--colorPaletteRedForeground1);
    }
    /* A file listed without a copy to compare with, or one a command may have been the only to touch. */
    .cv-changes-warn {
        color: var(--colorPaletteYellowForeground2, var(--colorNeutralForeground2));
    }

    .cv-changes-problems {
        margin-bottom: 6px;
        padding: 4px 6px;
        font-size: var(--fontSizeBase200);
        color: var(--colorPaletteRedForeground1);
        background: var(--colorPaletteRedBackground1);
        border-radius: var(--borderRadiusMedium);
    }
    .cv-changes-note {
        margin-top: 6px;
        font-size: var(--fontSizeBase200);
        color: var(--colorNeutralForeground3);
    }

    /* A small icon button that is a native <button>: the row has three of them for up to two hundred
     * files, and a fluent-button each would be a component instance and an inline <svg> apiece. The
     * values are fluent-button's own for subtle, small, icon-only, as in base.css. */
    .cv-icon-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        box-sizing: border-box;
        flex-shrink: 0;
        width: 22px;
        height: 24px;
        margin: 0;
        padding: 0;
        border: var(--strokeWidthThin) solid transparent;
        border-radius: var(--borderRadiusMedium);
        background-color: var(--colorSubtleBackground);
        color: var(--colorNeutralForeground2);
        font: inherit;
        cursor: pointer;
        user-select: none;
        outline-style: none;
        transition:
            background-color var(--durationFaster) var(--curveEasyEase),
            color var(--durationFaster) var(--curveEasyEase);
    }
    .cv-icon-btn:hover {
        background-color: var(--colorSubtleBackgroundHover);
        color: var(--colorNeutralForeground2Hover);
    }
    .cv-icon-btn:hover:active {
        background-color: var(--colorSubtleBackgroundPressed);
        color: var(--colorNeutralForeground2Pressed);
    }
    .cv-icon-btn:focus-visible {
        box-shadow:
            var(--shadow4),
            0 0 0 2px var(--colorStrokeFocus2);
    }
    .cv-icon-btn:disabled {
        color: var(--colorNeutralForegroundDisabled);
        background-color: var(--colorTransparentBackground);
        cursor: not-allowed;
    }
    /* An icon drawn by CSS from one image: it takes the colour of the text, so it follows the theme
     * and the hover of whatever holds it. 14px, the size every action icon is drawn at. */
    .cv-icon {
        display: block;
        flex-shrink: 0;
        width: 14px;
        height: 14px;
        background-color: currentColor;
        mask: var(--cv-icon) center / contain no-repeat;
    }
    /* @fluentui/svg-icons, the 16px regular set: the path is copied, and has to be again if the
     * package redraws one. */
    .cv-icon-open {
        --cv-icon: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M4.5 3C3.67 3 3 3.67 3 4.5v7c0 .83.67 1.5 1.5 1.5h7c.83 0 1.5-.67 1.5-1.5V9.27a.5.5 0 0 1 1 0v2.23a2.5 2.5 0 0 1-2.5 2.5h-7A2.5 2.5 0 0 1 2 11.5v-7A2.5 2.5 0 0 1 4.5 2h2.23a.5.5 0 0 1 0 1zm4.27-.5c0-.28.22-.5.5-.5h4.23c.28 0 .5.22.5.5v4.23a.5.5 0 0 1-1 0V3.71L9.62 7.08a.5.5 0 1 1-.7-.7L12.29 3H9.27a.5.5 0 0 1-.5-.5'/%3E%3C/svg%3E");
    }
    .cv-icon-checkmark {
        --cv-icon: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M13.86 3.66a.5.5 0 0 1-.02.7l-7.93 7.48a.6.6 0 0 1-.84-.02L2.4 9.1a.5.5 0 0 1 .72-.7l2.4 2.44 7.65-7.2a.5.5 0 0 1 .7.02'/%3E%3C/svg%3E");
    }
    .cv-icon-arrow-undo {
        --cv-icon: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M3 2.5a.5.5 0 0 1 1 0v3.84l3.17-3.17a4 4 0 0 1 5.66 5.66L7.8 13.85a.5.5 0 0 1-.7-.7l5.02-5.03a3 3 0 1 0-4.24-4.24L4.76 7H8.5a.5.5 0 0 1 0 1H3.6a.6.6 0 0 1-.6-.6z'/%3E%3C/svg%3E");
    }
    .cv-icon-warning {
        --cv-icon: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Cpath d='M5.82 2.28a2.5 2.5 0 0 1 4.36 0l4.5 8A2.5 2.5 0 0 1 12.5 14h-9a2.5 2.5 0 0 1-2.18-3.72zm3.49.48a1.5 1.5 0 0 0-2.62 0l-4.5 8a1.5 1.5 0 0 0 1.31 2.24h9a1.5 1.5 0 0 0 1.3-2.23zM8 9.5A.75.75 0 1 1 8 11a.75.75 0 0 1 0-1.5M8 5c.28 0 .5.22.5.5V8a.5.5 0 0 1-1 0V5.5c0-.28.22-.5.5-.5'/%3E%3C/svg%3E");
    }
`;

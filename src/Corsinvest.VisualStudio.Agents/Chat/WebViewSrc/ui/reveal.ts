/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// What the transcript only shows under the pointer or the focus is not kept in the DOM: one set of
// document listeners creates it on the way in and removes it on the way out. Kept per row, a copy
// button costs 12.6 nodes and 3.1 shadow roots, and a long transcript held hundreds of them, plus
// the actions row under every message. Nothing listens per row either: a thousand messages are a
// thousand marks, not four thousand listeners.
//
// Three marks:
//
// `data-copy`: a block that gets a copy button. `data-copy=""` copies the block's own <pre>; a
// value is the text to copy, for a block whose DOM does not hold it (a table's markdown source).
// A Lit template sets the `copyText` property instead, a reference to the string it already has
// rather than a copy in an attribute. The button lands in the block's `data-copy-slot` descendant
// when there is one, else in the block.
//
// `data-reveal`: a row that stays in the DOM empty, at the height chat.css reserves for it, and is
// filled from its `reveal` property (a function returning a Lit template) while the pointer or
// the focus is in its box. The box is the nearest BOX ancestor, the same one whose :hover fades
// the row in.
//
// Both take `tabindex="0"`, which is what lets the keyboard reach something that is not there yet:
// the mark takes the focus, its content appears, and the next Tab lands on it.
//
// `data-tip`: what something says about itself when pointed at. One tooltip for the whole page, moved
// to whatever is under the pointer. A `title` would cost as little, but the system draws it: in its
// own theme whatever Visual Studio's is, and after a wait long enough that an icon gets clicked to
// find out what it does. A `fluent-tooltip` per button is themed and prompt, and is a component
// with a shadow root on every row. An icon-only button named this way needs an `aria-label` too:
// `title` was also its accessible name. Two refinements: `data-tip-place="after"` opens it beside
// the target instead of above, for a control whose tooltip would otherwise cover the field being
// typed in; and a `tipContent` property (a function returning a Lit template) draws more than a
// string can, with `data-tip` still set so the target is found.

import { nothing, render } from 'lit';
import './components/cv-copy-btn';
import type { CvCopyBtn } from './components/cv-copy-btn';

type CopyBlock = HTMLElement & { copyText?: string };

let host: CopyBlock | null = null;
let btn: CvCopyBtn | null = null;

function textOf(block: CopyBlock): string {
    return block.copyText ?? block.dataset.copy ?? '';
}

/** What is under the pointer, innermost first, through every shadow root on the way: an event's
 *  composedPath, or the same thing worked out from a position (pathAt). */
type Path = readonly EventTarget[];

/** Innermost marked block on the path. */
function markedBlock(path: Path): HTMLElement | null {
    return (
        path.find(
            (n): n is HTMLElement => n instanceof HTMLElement && n.hasAttribute('data-copy'),
        ) ?? null
    );
}

function showCopy(next: CopyBlock | null): void {
    // isConnected too: a re-render of the block's parent drops the button without the pointer
    // ever leaving.
    if (next && next === host && btn?.isConnected) {
        // A tool still running grows its output under the pointer.
        btn.text = textOf(next);
        return;
    }
    btn?.remove();
    host = next;
    btn = null;
    if (!next) {
        return;
    }
    // A new one each time, never one moved around: the "copied" tick is the button's own state
    // and would follow it onto the next block.
    btn = document.createElement('cv-copy-btn');
    btn.text = textOf(next);
    btn.fromPre = btn.text ? '' : '1';
    (next.querySelector('[data-copy-slot]') ?? next).append(btn);
}

/** The boxes whose :hover and :focus-within reveal a row in chat.css. */
const BOX = 'cv-message, .cv-response, .cv-children';

type Reveal = () => unknown;
type RevealRow = HTMLElement & { reveal?: Reveal };

let hoverBoxes: Element[] = [];
let focusBoxes: Element[] = [];
/** The rows filled right now, with the function each was last filled from. */
const shown = new Map<RevealRow, Reveal | undefined>();

/** Every box on the path, innermost first: a sub-agent's children sit in a response, and both
 *  rows show, as both :hover rules match. */
function boxesOf(path: Path): Element[] {
    return path.filter((n): n is Element => n instanceof Element && n.matches(BOX));
}

/** The rows a box owns: marked, and with no nearer box in between. */
function rowsOf(box: Element): RevealRow[] {
    return [...box.querySelectorAll<RevealRow>('[data-reveal]')].filter(
        (row) => row.closest(BOX) === box,
    );
}

/** Looked up again on every event, not only when the boxes change: a response gets its row when
 *  its last block stops streaming, which can happen under a pointer that has not moved out. */
function syncRows(): void {
    const next = new Set<RevealRow>();
    for (const box of new Set([...hoverBoxes, ...focusBoxes])) {
        for (const row of rowsOf(box)) {
            next.add(row);
        }
    }
    for (const row of shown.keys()) {
        if (!next.has(row)) {
            render(nothing, row);
            shown.delete(row);
        }
    }
    for (const row of next) {
        // The owner hands over a new function whenever it re-renders: what the row shows (a
        // turn's figures landing, a queued message being read) can change in place too.
        if (!shown.has(row) || shown.get(row) !== row.reveal) {
            shown.set(row, row.reveal);
            render(row.reveal?.() ?? nothing, row);
        }
    }
}

/** How long the pointer rests on a control before its name appears. */
const TIP_DELAY_MS = 300;
/** And on anything else. Text that says more about itself (a command's description, a link's
 *  address) is read with the pointer resting on it: a tooltip that keeps up covers the next row of
 *  a list, or the line above the one being read. */
const TIP_SLOW_DELAY_MS = 800;
/** What is named promptly: a control, or a mark that asks for it because its values are read by
 *  sweeping across them (the cells of a chart). */
const TIP_PROMPT = 'button, fluent-button, fluent-switch, [data-tip-fast]';
/** Room between the tooltip and its target, and between the tooltip and the viewport's edge. */
const TIP_GAP_PX = 6;

type TipTarget = HTMLElement & { tipContent?: () => unknown };

let tipEl: HTMLElement | null = null;
let tipTarget: TipTarget | null = null;
/** The string the open tooltip was drawn from, to tell when it has changed in place. */
let tipShown = '';
let tipTimer = 0;
/** When the last tooltip closed: moving along a row of icons, or down a list once one row has
 *  said its piece, should not wait again on each. */
let tipClosedAt = -Infinity;
/** Where the pointer is, for a target too wide to centre on: a row, a link over two lines. */
let pointerX = -1;
let pointerY = -1;
/** One look per frame at what is under the pointer, however many moves the frame brought. */
let lookQueued = false;
/** The innermost element last looked at: a move inside it changes nothing. */
let lastTop: EventTarget | null = null;
/** A focus that follows a click is not a request for the name of what was clicked. */
let lastInputWasPointer = false;
/** The button just clicked: its name stays away until the pointer has left it, or moving a pixel
 *  across its icon would bring back what the click dismissed. */
let tipMuted: TipTarget | null = null;

function tipTargetOf(path: Path): TipTarget | null {
    return path.find((n): n is HTMLElement => n instanceof HTMLElement && !!n.dataset.tip) ?? null;
}

/** composedPath for a position instead of an event: the element there, then every ancestor,
 *  stepping out of each shadow root to its host. */
function pathAt(x: number, y: number): Element[] {
    let at = document.elementFromPoint(x, y);
    while (at?.shadowRoot) {
        const inner = at.shadowRoot.elementFromPoint(x, y);
        if (!inner || inner === at) {
            break;
        }
        at = inner;
    }
    const path: Element[] = [];
    for (let n: Node | null = at; n; n = n instanceof ShadowRoot ? n.host : n.parentNode) {
        if (n instanceof Element) {
            path.push(n);
        }
    }
    return path;
}

function tipIsOpen(): boolean {
    return !!tipEl?.matches(':popover-open');
}

/** Above the target, at the pointer when it is over it and centred otherwise (the keyboard's
 *  case); below when there is no room above. Beside it when the target asks, room allowing. */
function placeTip(): void {
    if (!tipEl || !tipTarget) {
        return;
    }
    const at = tipTarget.getBoundingClientRect();
    const own = tipEl.getBoundingClientRect();
    const beside = at.right + TIP_GAP_PX;
    if (tipTarget.dataset.tipPlace === 'after' && beside + own.width <= innerWidth - TIP_GAP_PX) {
        const middle = at.top + at.height / 2 - own.height / 2;
        const top = Math.max(TIP_GAP_PX, Math.min(middle, innerHeight - own.height - TIP_GAP_PX));
        tipEl.style.top = `${Math.round(top)}px`;
        tipEl.style.left = `${Math.round(beside)}px`;
        return;
    }
    const above = at.top - own.height - TIP_GAP_PX;
    const top = above >= TIP_GAP_PX ? above : at.bottom + TIP_GAP_PX;
    const over = pointerX >= at.left && pointerX <= at.right;
    const centred = (over ? pointerX : at.left + at.width / 2) - own.width / 2;
    const left = Math.max(TIP_GAP_PX, Math.min(centred, innerWidth - own.width - TIP_GAP_PX));
    tipEl.style.top = `${Math.round(top)}px`;
    tipEl.style.left = `${Math.round(left)}px`;
}

function openTip(): void {
    const text = tipTarget?.isConnected ? tipTarget.dataset.tip : '';
    if (!tipTarget || !text) {
        return;
    }
    if (!tipEl) {
        // A popover, so it sits in the top layer: over a modal dialog too, which is where the
        // plugin manager's icons are.
        tipEl = document.createElement('div');
        tipEl.className = 'cv-tip';
        tipEl.popover = 'manual';
        tipEl.setAttribute('role', 'tooltip');
        document.body.append(tipEl);
    }
    tipShown = text;
    // A template's own line breaks are layout, not text: only a plain string keeps them.
    tipEl.classList.toggle('rich', !!tipTarget.tipContent);
    render(tipTarget.tipContent?.() ?? text, tipEl);
    if (!tipIsOpen()) {
        tipEl.showPopover();
    }
    placeTip();
}

function closeTip(): void {
    clearTimeout(tipTimer);
    if (tipIsOpen()) {
        tipEl?.hidePopover();
        tipClosedAt = performance.now();
    }
    tipTarget = null;
}

function aimTip(next: TipTarget | null): void {
    if (next === tipTarget) {
        // The name can change in place: Copy becomes Copied, Read aloud becomes Pause. Only then:
        // this runs on every frame the pointer moves, and reopening would drag the tooltip along.
        if (next && tipIsOpen() && tipShown !== next.dataset.tip) {
            openTip();
        }
        return;
    }
    closeTip();
    if (next !== tipMuted) {
        tipMuted = null;
    }
    if (!next || next === tipMuted) {
        return;
    }
    tipTarget = next;
    const prompt = next.matches(TIP_PROMPT);
    const warm = performance.now() - tipClosedAt < TIP_DELAY_MS;
    tipTimer = window.setTimeout(openTip, warm ? 0 : prompt ? TIP_DELAY_MS : TIP_SLOW_DELAY_MS);
}

/** The pointer is over `path`: bring all three marks up to date. */
function hover(path: Path): void {
    lastTop = path[0] ?? null;
    showCopy(markedBlock(path));
    hoverBoxes = boxesOf(path);
    syncRows();
    aimTip(tipTargetOf(path));
}

/** Call once from init(). */
export function installReveal(): void {
    // pointerover covers what moves under a pointer that is still: a scroll, a row appearing.
    document.addEventListener('pointerover', (e) => hover(e.composedPath()));
    // It cannot be the only source. Measured in the WebView (runtime 154): over a list that
    // re-renders its rows as the pointer crosses them, and whenever the tooltip's popover is
    // showing, pointerover comes for one row in many or not at all, though pointermove keeps
    // coming and elementFromPoint still answers right. The tooltip named the first row and stayed
    // there. So what is under the pointer is also looked up from its position, once a frame, and
    // acted on when it is no longer the same element.
    document.addEventListener(
        'pointermove',
        (e) => {
            pointerX = e.clientX;
            pointerY = e.clientY;
            if (lookQueued) {
                return;
            }
            lookQueued = true;
            requestAnimationFrame(() => {
                lookQueued = false;
                const path = pathAt(pointerX, pointerY);
                if (path[0] !== lastTop) {
                    hover(path);
                }
            });
        },
        { passive: true },
    );
    document.documentElement.addEventListener('pointerleave', () => {
        showCopy(null);
        hoverBoxes = [];
        syncRows();
        closeTip();
    });
    // No focusout: the focusin that follows it says where the focus went, and stepping from a
    // mark to the content it has just been given resolves to the same block and the same boxes.
    document.addEventListener('focusin', (e) => {
        const path = e.composedPath();
        showCopy(markedBlock(path));
        focusBoxes = boxesOf(path);
        syncRows();
        if (!lastInputWasPointer) {
            aimTip(tipTargetOf(path));
        }
    });
    // A tooltip names what is about to be acted on: once it has been, or the page moves under
    // it, it is in the way.
    document.addEventListener(
        'pointerdown',
        (e) => {
            lastInputWasPointer = true;
            tipMuted = tipTargetOf(e.composedPath());
            closeTip();
        },
        true,
    );
    document.addEventListener(
        'keydown',
        () => {
            lastInputWasPointer = false;
            pointerX = -1;
            closeTip();
        },
        true,
    );
    document.addEventListener('scroll', closeTip, true);
}

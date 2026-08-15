/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/**
 * One second-clock for the whole WebView, for the badges that count upwards.
 *
 * Every running sub-agent draws two of those — one in its transcript row, one in the chip — so a
 * per-component timer means twice as many as there are agents, each started whenever its element
 * happened to mount. That showed: the same task's two badges read 28s and 29s at the same moment,
 * because their intervals had drifted apart. Sharing the tick makes them equal by construction.
 *
 * The subscription IS the reference count. There is no counter to keep in step with the
 * subscribers, so a component that forgets nothing but its own unsubscribe cannot leave the timer
 * running: when the last one goes, so does it.
 *
 * Not for one-shot delays (a "copied" label resetting, a notice dismissing itself): those want
 * their own precise timeout, and rounding them to a second would only make them late.
 */

type Listener = () => void;

/** What onTick hands back: call it to stop being ticked. Named so the field holding it reads as
 *  something to call rather than as an anonymous `() => void`. */
export type UnsubscribeTick = () => void;

const listeners = new Set<Listener>();
let timer = 0;

function fire(): void {
    // Copied first: a listener that unsubscribes while being notified would otherwise mutate the
    // set under the iteration.
    for (const fn of [...listeners]) {
        fn();
    }
}

function start(): void {
    if (timer || listeners.size === 0 || document.hidden) {
        return;
    }
    timer = window.setInterval(fire, 1000);
}

function stop(): void {
    if (!timer) {
        return;
    }
    clearInterval(timer);
    timer = 0;
}

// A pane sitting behind another one for an hour has nothing to redraw, and its badges are read
// again only when it comes back — so the clock stops with the pane and the first tick on return
// catches everything up. Done here once rather than in every component, which is why none of them
// did it.
document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
        stop();
    } else {
        fire();
        start();
    }
});

/**
 * Call `fn` about once a second. Returns the unsubscribe — call it from `disconnectedCallback`,
 * or whenever the badge stops needing a clock.
 *
 * The unsubscribe is returned rather than offered as an offTick(fn), because that one only works
 * if the caller hands back the very same function object: an arrow written twice is two functions,
 * and the delete would miss silently, leaving exactly the stuck timer this module exists to avoid.
 * It is also the shape bridge.onNotification already uses throughout the WebView.
 */
export function onTick(fn: Listener): UnsubscribeTick {
    listeners.add(fn);
    start();
    return () => {
        listeners.delete(fn);
        if (listeners.size === 0) {
            stop();
        }
    };
}

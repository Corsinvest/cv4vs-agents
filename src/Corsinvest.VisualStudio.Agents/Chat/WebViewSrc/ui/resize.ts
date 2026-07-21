/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Shared ResizeObserver: one app-wide instance; callers register per-element
// callbacks via `observeSize(el, fn)` instead of each creating their own RO.
import { logger } from '../core/logger';

const _callbacks = new Map<Element, (entry: ResizeObserverEntry) => void>();

const _ro = new ResizeObserver((entries) => {
    for (const e of entries) {
        const fn = _callbacks.get(e.target);
        if (fn) {
            try {
                fn(e);
            } catch (err) {
                logger.debug(
                    `resize.observe failed: ${err instanceof Error ? err.message : String(err)}`,
                );
            }
        }
    }
});

/**
 * Observe size changes of `el`; fires on initial observe and every change.
 * One callback per element — re-registering replaces it. Returns an unobserve fn.
 */
export function observeSize(el: Element, fn: (entry: ResizeObserverEntry) => void): () => void {
    _callbacks.set(el, fn);
    _ro.observe(el);
    return () => {
        _callbacks.delete(el);
        _ro.unobserve(el);
    };
}

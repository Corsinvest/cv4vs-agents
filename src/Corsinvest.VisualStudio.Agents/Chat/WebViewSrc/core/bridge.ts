/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Bridge between the WebView and the C# host. Two kinds of traffic (JSON-RPC-style):
//   - sendNotification()/onNotification(): fire-and-forget, no reply (the bulk).
//   - sendRequest(): correlated request/response — a monotonic `id` rides the envelope,
//     the host echoes it on the response, and a single _pending map resolves the Promise.
// start() wires the listener once. Handlers run in try/catch so one bad handler can't kill
// the dispatcher.

import { logger } from './logger';
import { Msg } from './bridge-messages';
import type { RequestType } from './request-types';

// Reject a request whose response never arrives, so _pending can't leak and the caller
// gets an error instead of an eternal spinner. Matches every production JSON-RPC client.
const REQUEST_TIMEOUT_MS = 30_000;

// chrome.webview is injected by WebView2 at runtime — not in lib.dom.
interface WebViewMessageEvent {
    data: unknown;
}
interface ChromeWebView {
    addEventListener(event: 'message', handler: (e: WebViewMessageEvent) => void): void;
    postMessage(message: unknown): void;
}
declare global {
    interface Window {
        chrome?: { webview?: ChromeWebView };
    }
}

// Streamed channels — emit too often for a useful trace, skip them.
// High-frequency channels excluded from bridge trace. Sourced from the generated message
// names (same constants the host's IsNoisyChannel uses) so the two lists can't drift.
const NOISY = new Set<string>([
    Msg.toWebView.chat.assistantTextDelta,
    Msg.toWebView.chat.toolProgress,
]);

function truncate(value: unknown): unknown {
    try {
        const s = JSON.stringify(value);
        if (!s || s.length <= 500) {
            return value;
        }
        return s.slice(0, 500) + '…';
    } catch {
        return value;
    }
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Handler<T = any> = (data: T) => void;

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type PendingReq = {
    resolve: (v: any) => void;
    reject: (e: Error) => void;
    timer: ReturnType<typeof setTimeout>;
};

class Bridge {
    private _handlers = new Map<string, Set<Handler>>();
    private _started = false;
    private _reqSeq = 0;
    private _pending = new Map<number, PendingReq>();

    /** Fire-and-forget message to the host (no reply expected). */
    sendNotification<T = unknown>(type: string, data: T = {} as T): void {
        this._postRaw(type, data);
    }

    /** Correlated request: sends with an id, returns a Promise that resolves with the host's
     *  response (or rejects on an error-response / timeout). One _pending map for all requests.
     *  `timeoutMs` overrides the default for heavy operations (e.g. the first full stats
     *  aggregation, which scans every project's .jsonl and can exceed 30s). */
    sendRequest<P, R>(
        rt: RequestType<P, R>,
        params: P,
        timeoutMs = REQUEST_TIMEOUT_MS,
    ): Promise<R> {
        const id = ++this._reqSeq;
        return new Promise<R>((resolve, reject) => {
            const timer = setTimeout(() => {
                this._pending.delete(id);
                reject(new Error(`bridge request timeout: ${rt.requestChannel}`));
            }, timeoutMs);
            this._pending.set(id, { resolve, reject, timer });
            this._postRaw(rt.requestChannel, params, id);
        });
    }

    /** Reject every in-flight request (session change / clear), so their Promises settle instead
     *  of resolving against the new session or hanging until timeout. */
    rejectAllPending(reason: string): void {
        for (const [, p] of this._pending) {
            clearTimeout(p.timer);
            p.reject(new Error(reason));
        }
        this._pending.clear();
    }

    private _postRaw(type: string, data: unknown, id?: number): void {
        const wv = window.chrome?.webview;
        if (!wv) {
            // eslint-disable-next-line no-console
            console.warn('[bridge] chrome.webview not available — message dropped:', type);
            return;
        }
        if (!NOISY.has(type)) {
            logger.trace(`bridge → host ${type}`, truncate(data));
        }
        wv.postMessage(id != null ? { type, data, id } : { type, data });
    }

    /** Dispatch a message locally, as if it had arrived from the host. Used to
     *  echo the user's own submitted message into the chat (the CLI doesn't
     *  reflect it back in stream-json), without a round-trip through C#. */
    emit(type: string, data: unknown = {}): void {
        this._dispatch({ type, data });
    }

    /** Subscribe to a notification channel. Returns an unsubscribe fn. */
    onNotification<T = unknown>(type: string, handler: Handler<T>): () => void {
        let set = this._handlers.get(type);
        if (!set) {
            set = new Set();
            this._handlers.set(type, set);
        }
        set.add(handler as Handler);
        return () => {
            set!.delete(handler as Handler);
        };
    }

    start(): void {
        if (this._started) {
            return;
        }
        const wv = window.chrome?.webview;
        if (!wv) {
            // eslint-disable-next-line no-console
            console.warn('[bridge] chrome.webview not available — start() is a no-op');
            return;
        }
        wv.addEventListener('message', (e) => this._dispatch(e.data));
        this._started = true;
    }

    private _dispatch(raw: unknown): void {
        let parsed: { type?: string; data?: unknown; id?: number; error?: string } | undefined;
        if (typeof raw === 'string') {
            try {
                parsed = JSON.parse(raw);
            } catch {
                // A non-JSON message on the wire is unexpected (everything should be JSON), so keep
                // it a direct console.warn — visible even at LogLevel=None, not gated as mere noise.
                // eslint-disable-next-line no-console
                console.warn('[bridge] non-JSON message ignored:', raw);
                return;
            }
        } else if (raw && typeof raw === 'object') {
            parsed = raw as { type?: string; data?: unknown; id?: number; error?: string };
        }
        if (!parsed?.type) {
            return;
        }
        if (!NOISY.has(parsed.type)) {
            logger.trace(`bridge ← host ${parsed.type}`, truncate(parsed.data));
        }
        // A message carrying an id is a request/response. Resolve/reject the pending Promise and
        // ALWAYS return — it must never fall through to the notification handlers (a response
        // channel like chat_history/subagent_loaded may still have onNotification listeners).
        if (parsed.id != null) {
            const p = this._pending.get(parsed.id);
            if (p) {
                clearTimeout(p.timer);
                this._pending.delete(parsed.id);
                if (parsed.error != null) {
                    p.reject(new Error(parsed.error));
                } else {
                    p.resolve(parsed.data);
                }
            } else {
                // id present but not pending: timed-out, duplicate, or stale response. Drop it —
                // do NOT reinterpret as a notification (would re-mutate the UI after the fact).
                logger.warn(
                    `bridge: response for unknown/expired id ${parsed.id} (${parsed.type}) — dropped`,
                );
            }
            return;
        }
        const set = this._handlers.get(parsed.type);
        if (!set) {
            return;
        }
        for (const h of set) {
            try {
                h(parsed.data);
            } catch (err) {
                // eslint-disable-next-line no-console
                console.error(`[bridge] handler for "${parsed.type}" threw:`, err);
            }
        }
    }
}

export const bridge = new Bridge();

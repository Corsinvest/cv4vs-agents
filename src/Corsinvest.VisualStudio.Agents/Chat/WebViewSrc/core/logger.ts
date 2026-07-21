/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// WebView logging entry point. Level gate + independent perf-timing gate,
// both driven by the host's `init` payload (see ui/init.ts).

export enum LogLevel {
    None = 0,
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

class Logger {
    private _level: LogLevel = LogLevel.None;
    private _perfEnabled = false;
    private _spans = new Map<string, number>();

    setLevel(level: LogLevel): void {
        this._level = level;
    }

    setPerfEnabled(on: boolean): void {
        this._perfEnabled = on;
    }

    error(msg: string, ...args: unknown[]): void {
        if (this._level >= LogLevel.Error) {
            // eslint-disable-next-line no-console
            console.error(this._fmt('ERROR', msg), ...args);
        }
    }

    warn(msg: string, ...args: unknown[]): void {
        if (this._level >= LogLevel.Warn) {
            // eslint-disable-next-line no-console
            console.warn(this._fmt('WARN', msg), ...args);
        }
    }

    info(msg: string, ...args: unknown[]): void {
        if (this._level >= LogLevel.Info) {
            // eslint-disable-next-line no-console
            console.info(this._fmt('INFO', msg), ...args);
        }
    }

    debug(msg: string, ...args: unknown[]): void {
        if (this._level >= LogLevel.Debug) {
            // eslint-disable-next-line no-console
            console.log(this._fmt('DEBUG', msg), ...args);
        }
    }

    trace(msg: string, ...args: unknown[]): void {
        if (this._level >= LogLevel.Trace) {
            // eslint-disable-next-line no-console
            console.log(this._fmt('TRACE', msg), ...args);
        }
    }

    perf(msg: string): void {
        if (!this._perfEnabled) {
            return;
        }
        // eslint-disable-next-line no-console
        console.log(this._fmt('PERF', msg));
    }

    perfStart(label: string): void {
        if (!this._perfEnabled) {
            return;
        }
        this._spans.set(label, performance.now());
    }

    perfEnd(label: string, extra?: string): void {
        if (!this._perfEnabled) {
            return;
        }
        const t0 = this._spans.get(label);
        if (t0 === undefined) {
            return;
        }
        this._spans.delete(label);
        const ms = (performance.now() - t0).toFixed(0);
        this.perf(`${label} ${ms}ms${extra ? ' ' + extra : ''}`);
    }

    private _fmt(level: string, msg: string): string {
        return `${new Date().toISOString().slice(11, 23)} ${level} ${msg}`;
    }
}

export const logger = new Logger();

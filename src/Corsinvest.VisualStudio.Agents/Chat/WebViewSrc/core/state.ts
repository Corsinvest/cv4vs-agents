/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Shared WebView state: a Proxy over a private data object. Read/write via
// natural property syntax; writes notify subscribers (state.on), skipping
// no-op assignments. Defaults below mirror AgentsOptions.cs (most fields
// arrive via the host `init` payload) — keep the two in sync.

import type {
    ContextUsageDto,
    EffortLevelDto,
    IdeContextNotification,
    ModelInfoDto,
    PendingPermission,
    PermissionMode,
    SlashCommandDto,
    SubagentTask,
    Theme,
    VsOptionsConfig,
} from './types';

interface AppState {
    workingDirectory: string;
    currentModel: string | null;
    permissionMode: PermissionMode;
    /** Command list from the CLI's `initialize` catalogue / `commands_changed`
     *  (name + description + hint + aliases). Empty until it arrives; drives the `/` menu. */
    slashCommands: SlashCommandDto[];
    theme: Theme;
    /** Reasoning effort level for the Model menu slider. */
    effortLevel: EffortLevelDto;
    /** ultracode flag: the slider's purple stop past xhigh (effort=xhigh + this). */
    ultracodeEnabled: boolean;
    /** Extended thinking on/off (Model menu toggle). */
    thinkingEnabled: boolean;
    /** Host built in DEBUG (developer running under VS). Gates dev-only diagnostics (e.g. raw status
     *  on the spinner). From the init payload; false in Release. */
    inDev: boolean;
    /** Fast mode on/off (Model menu toggle). */
    fastMode: boolean;
    /** Auto-switch model when a message is flagged (Model menu toggle). */
    switchModelsOnFlag: boolean;

    isBusy: boolean;
    // Raw CLI work status (system/status): "compacting" while compacting, "" otherwise. The spinner
    // maps known values to a fixed label (compacting → "Compacting…"), else the random working verb.
    status: string;
    pendingPermission: PendingPermission | null;
    initialized: boolean;
    // Active sub-agents, mirrored from cv-app's Map for cross-component access.
    subagentTasks: SubagentTask[];

    // History paging (scroll-up fetches older pages on demand):
    // - currentSessionId tags pages so out-of-order replies for a replaced session are dropped.
    // - oldestLoadedOffset: byte offset of oldest held message, sent as next beforeOffset.
    // - hasMoreHistory: false once the host reaches the start of the JSONL.
    // - loadingOlder: in-flight lock debouncing repeated scroll triggers.
    currentSessionId: string | null;
    oldestLoadedOffset: number;
    hasMoreHistory: boolean;
    loadingOlder: boolean;

    // Token usage of the most recent main-thread assistant message (NOT
    // sub-agents), in the CLI wire format. `null` until the first one arrives.
    contextUsage: ContextUsageDto | null;

    // Model catalogue from the CLI (`chat_models`); drives the picker and the
    // effort slider. Includes disabled models. Empty until the first init.
    models: ModelInfoDto[];
    // Current model's context window + max output, from the result's modelUsage.
    // 0 until the first turn completes — the gauge stays hidden until then.
    contextWindow: number;
    maxOutputTokens: number;

    // IDE context — driven by `ide_selection_changed` from the host.
    // `ideContextEnabled` is the eye-toggle on the badge: when false the
    // badge is hidden and the next prompt won't carry an <ide_*> tag.
    ideContext: IdeContextNotification | null;
    ideContextEnabled: boolean;

    // UI options from the C# Options page, as one group (the init payload's
    // `vsOptions` block, also re-pushed via `vs_settings`). Read at render time
    // (not observed per-field). Adding an option = one field in VsOptionsConfig, nothing here.
    ui: VsOptionsConfig;
}

type Listener<T> = (value: T) => void;
type OnFn<T extends object> = <K extends keyof T>(key: K, fn: Listener<T[K]>) => () => void;

/**
 * Internal store: holds the private data, dispatches change notifications.
 * Consumers don't touch this directly — they go through the Proxy below.
 */
class StoreImpl<T extends object> {
    private _data: T;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    private _subs = new Map<keyof T, Set<Listener<any>>>();

    constructor(initial: T) {
        this._data = { ...initial };
    }

    /** Used by the Proxy `get` trap. */
    _read<K extends keyof T>(key: K): T[K] {
        return this._data[key];
    }

    /** Used by the Proxy `set` trap. Skips work and skips notify on no-op. */
    _write<K extends keyof T>(key: K, value: T[K]): void {
        if (this._data[key] === value) {
            return;
        }
        this._data[key] = value;
        const subs = this._subs.get(key);
        if (!subs) {
            return;
        }
        for (const fn of subs) {
            try {
                fn(value);
            } catch (e) {
                console.error(`[state.on:${String(key)}]`, e);
            }
        }
    }

    /**
     * Subscribe to changes of a key. Returns an unsubscribe function.
     * The callback is NOT invoked with the current value — only with
     * future changes; read `state.<key>` if you need the current value.
     */
    on: OnFn<T> = (key, fn) => {
        let s = this._subs.get(key);
        if (!s) {
            s = new Set();
            this._subs.set(key, s);
        }
        s.add(fn);
        return () => {
            s!.delete(fn);
        };
    };
}

/**
 * Public store type: every AppState field is directly accessible (read/
 * write) plus the `on` subscribe method.
 */
type Store<T extends object> = T & { on: OnFn<T> };

const _impl = new StoreImpl<AppState>({
    workingDirectory: '',
    currentModel: null,
    permissionMode: 'default',
    slashCommands: [],
    theme: 'dark',
    effortLevel: 'high',
    ultracodeEnabled: false,
    thinkingEnabled: false,
    inDev: false,
    fastMode: false,
    switchModelsOnFlag: true, // default on, like VS Code (get_settings overrides)

    isBusy: false,
    status: '',
    pendingPermission: null,
    initialized: false,
    subagentTasks: [],

    currentSessionId: null,
    oldestLoadedOffset: -1,
    hasMoreHistory: false,
    loadingOlder: false,
    contextUsage: null,
    models: [],
    contextWindow: 0,
    maxOutputTokens: 0,

    ideContext: null,
    ideContextEnabled: true,

    ui: {
        showCostAndDuration: false,
        previewLines: 3,
        chatFontSize: 13,
        showRelativePaths: true,
        stickyUserMessages: true,
        showInlineToolErrors: false,
        useCtrlEnterToSend: false,
        compactOutputAskAnswers: true,
        allowDangerouslySkipPermissions: false,
        diffContextLines: 10,
        diffIgnoreWhitespace: false,
        showOpenDiffInVsButton: true,
        allowedUploadExtensions: [],
        appVersion: '',
        appCopyright: '',
        logLevel: 0,
        perfEnabled: false,
    },
});

export const state: Store<AppState> = new Proxy(_impl, {
    get(target, key) {
        if (key === 'on') {
            return target.on;
        }
        return target._read(key as keyof AppState);
    },
    set(target, key, value) {
        target._write(key as keyof AppState, value);
        return true;
    },
}) as unknown as Store<AppState>;

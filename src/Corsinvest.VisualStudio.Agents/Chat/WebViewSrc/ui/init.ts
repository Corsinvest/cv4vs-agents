/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// One-shot UI bootstrap: registers always-on bridge handlers, wires
// document-level state observers, and starts the bridge listener.

import { bridge } from '../core/bridge';
import { Msg } from '../core/bridge-messages';
import { state } from '../core/state';
import type { IdeContextNotification } from '../core/types';
import { PERMISSION_MODE } from '../core/types';
import { normPath } from '../core/path';
import { closeTopDialog } from '../core/dialog-focus';
import { setExtraLinkableExtensions } from '../core/file-links';
import type {
    CliStateNotification,
    HostKeyNotification,
    FilesDroppedNotification,
    InitPayloadNotification,
    ModelsNotification,
    PermissionMode,
    PermissionModeChangedNotification,
    ModelChangedNotification,
    SetComposerNotification,
    SlashCommandsNotification,
    ThemeChangedNotification,
    ExchangeEndedNotification,
    Theme,
    VsOptionsDto,
} from '../core/types';
import { applyHostKey } from './host-keys';
import { installDebugApi } from './debug';
import { logger } from '../core/logger';
import { initFluent, applyFluentTheme, applyFontScale } from './fluent';
import { setVerbsConfig } from './components/cv-spinner';

function applyTheme(theme: Theme): void {
    const light = theme === 'light';
    applyFluentTheme(!light);
    // Toggle which highlight.js theme <link> is live (both exist in index.html).
    const dark = document.getElementById('hljs-theme-dark') as HTMLLinkElement | null;
    const lightLink = document.getElementById('hljs-theme-light') as HTMLLinkElement | null;
    if (dark) {
        dark.disabled = light;
    }
    if (lightLink) {
        lightLink.disabled = !light;
    }
}

// Applies the VS Options category (init payload's `vsOptions` and the standalone
// `vs_settings` re-push share this — both carry the full VsOptionsDto).
function applyVsOptions(o: VsOptionsDto): void {
    state.ui = o;
    logger.setLevel(state.ui.logLevel ?? 0);
    logger.setPerfEnabled(!!state.ui.perfEnabled);
    document.body.classList.toggle('sticky-user', !!o.stickyUserMessages);
    applyFontScale(o.chatFontSize);
    // The file-link parser keeps its own set (module-level, not read from state at render time).
    setExtraLinkableExtensions(o.extraLinkableExtensions);
}

function wireBridgeHandlers(): void {
    // Pane config + VS options: everything the host has without asking the CLI. Arrives right
    // after ui_ready, ahead of the first history, so paths are shortened from the very first row.
    bridge.onNotification<InitPayloadNotification>(Msg.toWebView.ui.init, (data) => {
        if (!data?.config) {
            return;
        }
        state.workingDirectory = normPath(data.config.workingDirectory);
        state.inDev = data.config.inDev;

        if (data.vsOptions) {
            applyVsOptions(data.vsOptions);
            // The welcome screen reads appVersion/appCopyright from state at render time, but
            // state.ui is not observable — it was rendered once before this payload arrived, so
            // without a nudge it keeps the empty seed until some other event re-renders it (which
            // is why New Session made the copyright appear). Re-render it now.
            (
                document.querySelector('cv-welcome') as
                    (HTMLElement & { requestUpdate?(): void }) | null
            )?.requestUpdate?.();
        }
        state.initialized = true;
    });

    // The CLI's startup state, seconds behind the rest: claude.exe has to answer initialize +
    // get_settings first. Re-sent on every respawn, which is the reliable source for these.
    bridge.onNotification<CliStateNotification>(Msg.toWebView.cli.state, (data) => {
        const c = data?.cliState;
        if (!c) {
            return;
        }
        state.currentModel = c.model;
        state.permissionMode = (c.permissionMode as PermissionMode) || PERMISSION_MODE.default;
        if (c.effortLevel) {
            state.effortLevel = c.effortLevel;
        }
        state.ultracodeEnabled = !!c.ultracode;
        state.thinkingEnabled = !!c.alwaysThinkingEnabled;
        if (c.switchModelsOnFlag !== null && c.switchModelsOnFlag !== undefined) {
            state.switchModelsOnFlag = c.switchModelsOnFlag;
        }
        // Absent → false: a missing policy means the mode is allowed.
        state.bypassPermissionsDisabled = !!c.bypassPermissionsDisabled;
        state.fastMode = (c.fastModeState ?? 'off') !== 'off';
        // Custom spinner verbs from settings (replace/append the defaults). Migrated
        // from vsOptions into cliState — applied here rather than in applyVsOptions.
        setVerbsConfig(c.spinnerVerbsConfig ?? null);
    });

    // VS Options re-pushed standalone when the user changes the Options page while
    // the pane is open (independent of CLI state / a respawn).
    bridge.onNotification<VsOptionsDto>(Msg.toWebView.ui.vsSettings, (data) => {
        if (data) {
            applyVsOptions(data);
        }
    });

    bridge.onNotification<ThemeChangedNotification>(Msg.toWebView.ui.themeChanged, (data) => {
        const theme: Theme = data.dark ? 'dark' : 'light';
        state.theme = theme;
        applyTheme(theme);
    });

    // Host asks to focus the pane's input (session switch, or "Go to pane" from an attention
    // notification). If an ask/permission is open, land on it (its first choice) so the user can
    // answer immediately — the textarea is hidden while a permission is pending.
    bridge.onNotification(Msg.toWebView.ui.focusInput, () => {
        if (state.pendingPermission) {
            const banner = document.querySelector('cv-permission-banner') as
                import('./components/cv-permission-banner').CvPermissionBanner | null;
            if (banner?.focusFirst()) {
                return;
            }
        }
        const input = document.querySelector('cv-prompt') as
            import('./components/cv-prompt').CvPrompt | null;
        input?.focusInput();
    });

    // Forked pane: pre-fill the composer with the forked-at message text.
    bridge.onNotification<SetComposerNotification>(Msg.toWebView.ui.setComposer, (data) => {
        // The host re-opened the eye; mirror it so the badge shows what the CLI is being sent.
        if (data?.enableIdeContext) {
            state.ideContextEnabled = true;
        }
        const input = document.querySelector('cv-prompt') as
            import('./components/cv-prompt').CvPrompt | null;
        const text = data?.text ?? '';
        if (data?.send && text) {
            input?.setComposerTextAndSubmit(text);
        } else {
            input?.setComposerText(text);
        }
    });

    // A key the composition control dropped, claimed by the pane on our behalf — see host-keys.ts.
    bridge.onNotification<HostKeyNotification>(Msg.toWebView.ui.hostKey, (data) => {
        if (data?.key) {
            applyHostKey(data);
        }
    });

    // Rebuilt as File objects so the composer attaches them through its own path — allow-list and
    // rejection notice stay in one place.
    bridge.onNotification<FilesDroppedNotification>(Msg.toWebView.ui.filesDropped, (data) => {
        const files = (data?.files ?? []).map((f) => {
            const bin = atob(f.base64);
            const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0));
            return new File([bytes], f.name, { type: f.mediaType });
        });
        if (files.length > 0) {
            const input = document.querySelector('cv-prompt') as
                import('./components/cv-prompt').CvPrompt | null;
            void input?.addDroppedFiles(files);
        }
    });

    // Esc: VS routed its Cancel command to the pane (ChatPaneWindow claims it so VS
    // doesn't move focus to an open editor). Mirror the in-WebView Esc behaviour.
    bridge.onNotification(Msg.toWebView.ui.escape, () => {
        // A modal <dialog> (opened with showModal) only auto-closes on a REAL Esc — a
        // synthetic keydown can't trigger it. Since VS ate the real key, close the open
        // dialog ourselves. Each dialog registers its close() in an open-dialog stack
        // (dialog-focus.ts), so this works whether the <fluent-dialog> is in the light
        // DOM or a component's shadow root — no global querySelector needed.
        if (closeTopDialog()) {
            return;
        }
        // Otherwise mirror Esc for menus / cv-app's global stop handler.
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    });

    // Both selectors switch optimistically and the host echoes back what the CLI really holds —
    // on success the same value, on failure the previous one, which rolls the UI back. The
    // permission one also arrives unprompted, when the CLI changes mode by itself (a plan
    // approved, Shift+Tab from a remote terminal): same message, no caller waiting for it.
    bridge.onNotification<PermissionModeChangedNotification>(
        Msg.toWebView.cli.permissionModeChanged,
        (data) => {
            if (data?.mode) {
                state.permissionMode = data.mode;
            }
        },
    );

    bridge.onNotification<ModelChangedNotification>(Msg.toWebView.cli.modelChanged, (data) => {
        // Unlike the mode, an empty model is meaningful — it is "Default" — so only a missing
        // payload is ignored, never a null value.
        if (data) {
            state.currentModel = data.model || null;
        }
    });

    // Turn end: clear busy and capture the model's real context window/max output
    // (the result carries them; 0 means unknown, so keep the previous value).
    bridge.onNotification<ExchangeEndedNotification>(Msg.toWebView.chat.exchangeEnded, (data) => {
        state.isBusy = false;
        if (data?.contextWindow && data.contextWindow > 0) {
            state.contextWindow = data.contextWindow;
            state.maxOutputTokens = data.maxOutputTokens ?? 0;
        }
    });
    bridge.onNotification(Msg.toWebView.cli.exited, () => (state.isBusy = false));

    // Active editor file/selection for the context badge; empty filePath clears it.
    bridge.onNotification<IdeContextNotification>(Msg.toWebView.ide.selectionChanged, (data) => {
        state.ideContext = data?.filePath ? data : null;
    });

    // Rich slash-command list from the CLI's `commands_changed` (name +
    // description + hint). Replaces the cached list — the CLI re-pushes the
    // full set on every change, so we never merge.
    bridge.onNotification<SlashCommandsNotification>(Msg.toWebView.chat.slashCommands, (data) => {
        state.slashCommands = Array.isArray(data?.commands) ? data.commands : [];
    });

    // Model catalogue from the CLI's `initialize` (value, effort levels, flags).
    bridge.onNotification<ModelsNotification>(Msg.toWebView.chat.models, (data) => {
        state.models = Array.isArray(data?.models) ? data.models : [];
    });
}

/**
 * Bootstrap the UI layer. Call once from index.ts after the component
 * modules have been imported (so customElements are registered).
 */
export function init(): void {
    initFluent();

    // Seed Fluent luminance from state.theme so the first <fluent-*> render
    // matches before the host's theme_changed message arrives.
    applyFluentTheme(state.theme !== 'light');

    state.on('theme', applyTheme);

    wireBridgeHandlers();
    installDebugApi();
    bridge.start();

    // Tell the host the app has mounted and painted its first frame, so it can hide the native
    // "Initializing…" placeholder exactly when the chat is visible underneath — no white gap, no
    // double placeholder. Two frames, because the first only schedules the initial render.
    // Announced from here rather than on receiving ui_init: the host now answers this signal WITH
    // ui_init, and waiting for the payload to declare ourselves ready would deadlock the pair.
    requestAnimationFrame(() =>
        requestAnimationFrame(() => bridge.sendNotification(Msg.fromWebView.ui.ready, {})),
    );
}

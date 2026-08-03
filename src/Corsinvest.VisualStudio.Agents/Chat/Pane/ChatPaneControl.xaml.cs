/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Ide;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Corsinvest.VisualStudio.Agents.Chat.Pane;

public partial class ChatPaneControl : PaneControlBase
{
    public override bool SupportsTitleEditing => true;

    /// <summary>New instance per call, keyed on the pane's current working directory
    /// (constant for the pane's lifetime). Cheap enough that a shared field would add nothing.</summary>
    private SessionManager Sessions => new(PaneClaudePaths, Entry.WorkingDirectory);

    /// <summary>Re-read the freshest title (custom/ai/last-prompt) for the current
    /// session from its JSONL. Called on load/fork and at turn end so a generated
    /// or refined ai-title shows up. A user rename writes a custom-title, which the
    /// scan returns with top priority — so this never clobbers a manual rename.</summary>
    private void RefreshTitleFromDisk()
    {
        var sid = _client?.SessionId;
        var wd = _client?.WorkingDirectory;
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(wd)) { return; }
        var title = Sessions.ScanTitle(sid);
        if (!string.IsNullOrWhiteSpace(title)) { SetSessionTitle(title); }
    }

    public override void RenameSession(string newTitle)
    {
        var sid = _client?.SessionId;
        var wd = _client?.WorkingDirectory;
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(wd) || string.IsNullOrWhiteSpace(newTitle)) { return; }

        // Show it straight away — the write below is the slow part, and it can't fail in a way
        // the user could act on.
        SetSessionTitle(newTitle);

        // A live CLI holds the JSONL open and keeps the title in memory: appending to the file
        // behind its back races its own writes and leaves it believing the old title. Ask it to
        // rename instead, and let it persist. Only write the file ourselves when there is no CLI
        // to compete with, or when it is too old to know the request.
        if (_client?.IsRunning != true)
        {
            Sessions.Rename(sid, newTitle);
            return;
        }
        _ = RenameThroughClientAsync(sid, newTitle);
    }

    private async Task RenameThroughClientAsync(string sessionId, string newTitle)
    {
        try
        {
            if (await _client.RenameSessionAsync(newTitle)) { return; }
        }
        catch (Exception ex)
        {
            _log.LogException("[chat] rename_session", ex);
        }
        Sessions.Rename(sessionId, newTitle);
    }

    /// <summary>Fresh conversation in THIS pane. Mirrors the Session.New
    /// handler: clear the transcript, then start a new client session.</summary>
    public override void NewSession()
    {
        _bridge?.Send(BridgeMessages.ToWebView.Chat.Cleared, null);
        SetSessionTitle(null); // fresh chat: no title until the first turn generates one
        // The new session keeps the pane's current model/mode (NewSessionAsync reuses the
        // client's Model/PermissionMode); the respawn's system/init re-arms the gate, which
        // re-populates the selector — no seed push needed here.
        _ = _client?.NewSessionAsync();
        // Nothing else focuses the composer here: the pane is already active, so the frame doesn't
        // change and PaneWindowBase's activation path never runs. Without this the user has to
        // click into an empty chat before typing.
        FocusInput();
    }

    /// <summary>Resume a past session in THIS pane: clear the transcript,
    /// load its history into the WebView, then resume the client. (Was the old
    /// Session.Load bridge handler — the toolbar's History dropdown now calls
    /// this directly.) Permission mode / model reach the selector via the gate's
    /// ui_init, re-armed by the respawn's fresh system/init.</summary>
    public override void LoadSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || _client == null) { return; }
        _bridge?.Send(BridgeMessages.ToWebView.Chat.Cleared, null);
        var (mode, page, info) = ReadSessionState(sessionId);
        SendHistoryPage(page, sessionId);
        SetSessionTitle(info?.CustomTitle ?? info?.AiTitle ?? info?.LastPrompt);
        // Pass the session's own mode so the respawned CLI runs on what
        // the selector shows (not the client's leftover state). Model isn't
        // passed: --resume re-emits the session's own model via init.
        _ = _client.ResumeSessionAsync(sessionId, mode);
    }

    /// <summary>Read a session once: its permission mode (the CLI doesn't restore this from
    /// --resume, so it must be re-sent explicitly), plus the first transcript page and its info.
    /// Model is NOT read here — it comes from the CLI's own init re-emit on resume, via the gate.
    /// The caller decides WHEN to push each piece to the WebView (order differs between InitAsync
    /// and LoadSession). Shared so auto-resume and fork restore the same state a menu-picked
    /// session does. Reads the workdir off the entry (constant, valid even before _client exists —
    /// InitAsync calls this pre-client).</summary>
    private (string mode, SessionManager.HistoryPage page, SessionInfo info) ReadSessionState(string sessionId)
    {
        var page = Sessions.ReadHistoryRaw(sessionId, SessionManager.HistoryBatchSize, -1, out var info);
        var mode = info?.PermissionMode ?? "default";
        return (mode, page, info);
    }

    /// <summary>Push the boot state the host owns outright — pane config and VS options. Neither
    /// waits on claude.exe, so this goes out the moment the WebView is up, ahead of any history.
    /// The CLI's own state follows on cli_state when it answers.</summary>
    private void SendUiInit()
        => _bridge?.Send(BridgeMessages.ToWebView.Ui.Init, new Contracts.InitPayloadNotification
        {
            Config = new Contracts.InitConfigDto
            {
                WorkingDirectory = Entry.WorkingDirectory ?? "",
#if DEBUG
                InDev = true,
#endif
            },
            VsOptions = WebViewBridge.BuildVsOptions(),
        });

    /// <summary>Send a loaded history page to the WebView (messages + paging), then kick off the
    /// input ↑/↓ prompt history load in the background. Loading a session's transcript always loads
    /// its prompt history too — same act; both read the entry's constant workdir.</summary>
    private void SendHistoryPage(SessionManager.HistoryPage page, string sessionId)
    {
        if (page?.Messages == null) { return; }
        var events = HistoryReplay.ReplayPage(page.Messages, AgentsOptions.Chat.PreviewLines);
        // Sub-agent children are loaded lazily on expand (chevron → preview, Show all → full), the
        // same for the initial page and scroll-up — no preview is pre-appended here anymore.
        // Unprompted push (not a getHistory response) → notification channel, no id.
        _bridge?.Send(BridgeMessages.ToWebView.Chat.HistoryLoaded, new Contracts.HistoryLoadedNotification
        {
            Events = [.. events],
            SessionId = sessionId,
            OldestOffset = page.OldestOffset,
            HasMore = page.HasMore,
        });
        LoadPromptHistory(sessionId);
    }

    /// <summary>Read the typed-prompt history off the UI thread (it's lightweight but
    /// scans the file), then push it to the WebView's input ↑/↓ history. Fire-and-forget:
    /// the chat renders immediately; the prompt history arrives a moment later. Reads the entry's
    /// constant workdir, so it works even before _client exists (InitAsync resume-path).</summary>
    private void LoadPromptHistory(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var prompts = await Task.Run(() => Sessions.ReadUserPrompts(sessionId));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // Drop a stale result if the user switched sessions while we were reading.
            if (_client?.SessionId == sessionId || _client?.SessionId == null)
            {
                _bridge?.Send(BridgeMessages.ToWebView.Chat.PromptHistory, new Contracts.PromptHistoryNotification { SessionId = sessionId, Prompts = [.. prompts] });
            }
        }).FileAndForget(nameof(ChatPaneControl));
    }

    /// <summary>Focus = the WebView prompt box. Give the WebView2 control the native
    /// focus FIRST (else JS element.focus() only blinks a caret while keys go to VS),
    /// then let the JS handler focus the textarea on the ui_focus_input message.</summary>
    public override void FocusInput()
    {
        _bridge?.FocusWebView();
        _bridge?.Send(BridgeMessages.ToWebView.Ui.FocusInput, null);
    }

    /// <summary>Open the WebView2 native find bar (Ctrl+F), invoked by ChatPaneWindow when it
    /// intercepts the Find command from VS. Returns false if the WebView isn't ready.</summary>
    internal bool ShowFind()
    {
        if (_bridge == null) { return false; }
        _bridge.ShowFind();
        return true;
    }

    /// <summary>Handle Esc, invoked by ChatPaneWindow when it intercepts the Cancel
    /// command from VS. Without this VS treats Esc as "return to editor" and moves
    /// focus to an open document instead of letting the chat consume it (stop
    /// generation / close a menu). Forwarded to the WebView, which decides what to do.
    /// Returns false if the WebView isn't ready.</summary>
    internal bool HandleEscape()
    {
        // A WPF popup over the pane (the session picker) owns Esc first: VS routes Esc through
        // IOleCommandTarget, so the popup never sees the key on its own.
        if (Entry?.DismissHistoryAction?.Invoke() == true) { return true; }
        if (_bridge == null) { return false; }
        _bridge.Send(BridgeMessages.ToWebView.Ui.Escape, null);
        return true;
    }

    /// <summary>Only while the transport is up: a dead client keeps the exited process's id, which
    /// would show as a live PID pointing at nothing (or at whatever reused the number).</summary>
    protected override int CliProcessId => _client is { IsRunning: true } c ? c.Pid : 0;

    /// <summary>The WebView processes behind this pane, for the info dialog (the CLI's own PID is
    /// the base's shared row). What a frozen or misbehaving chat comes down to is "which process is
    /// mine" — with several panes open that is otherwise a command-line hunt in Task Manager, so
    /// the renderer is the pane's own, resolved by frame, not the browser's whole list.</summary>
    protected override IEnumerable<(string Label, string Value)> ExtraSessionInfo
    {
        get
        {
            yield return ("WebView PID", _bridge?.BrowserProcessId?.ToString() ?? "(not started)");
            // Blocking on the UI thread, with a deadline. The round-trip is in-process and
            // normally instant, but the answer comes back through the browser — which needs this
            // same thread to deliver it. A renderer that is busy or gone therefore hangs the IDE
            // on a row that is only diagnostic. Two seconds, then "(unknown)": nobody opens this
            // dialog for the renderer PID badly enough to freeze Visual Studio over it.
            var renderer = _bridge == null
                ? null
                : ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    var call = _bridge.RendererProcessIdAsync();
                    var done = await Task.WhenAny(call, Task.Delay(2000)).ConfigureAwait(true);
                    if (done != call)
                    {
                        _log.Warn("[chat] renderer PID timed out — WebView not answering");
                        return null;
                    }
                    return await call.ConfigureAwait(true);
                });
            yield return ("WebView renderer", renderer?.ToString() ?? "(unknown)");
        }
    }

    /// <summary>Chat-only extras for the toolbar's "More" menu — the WebView DevTools and the
    /// browser's task manager — on preview and Debug builds. Testers on a release candidate are
    /// exactly who needs to inspect the WebView to report a bug, and so is anyone building the
    /// extension — the DEBUG arm is what keeps them around once a version drops its -rc suffix,
    /// which is when they silently disappeared. A stable Marketplace build, which is neither, hides
    /// them unless the user opts in from Options: a chat that renders wrongly or stops taking input
    /// can only be diagnosed from the browser console, and that happens on stable builds too.
    /// (Info is shared, so it lives on the base; the CLI returns none.)</summary>
    public override IEnumerable<ButtonAction> MoreMenuActions
    {
        get
        {
#if DEBUG
            const bool devBuild = true;
#else
            const bool devBuild = false;
#endif
            if (BuildInfo.IsPreRelease || devBuild || AgentsOptions.Chat.ShowWebViewDevEntries)
            {
                yield return new ButtonAction("WebView DevTools", () => _bridge?.OpenDevTools(), "DevTools");
                yield return new ButtonAction("WebView task manager", () => WebView.OpenTaskManager(), "TaskManager");
            }
        }
    }

    // Resolved per line, not captured: Entry (and its PaneId) is injected after construction.
    // Assigned in the constructor because a field initializer can't reach an instance member.
    private readonly OutputWindowLogger _log;
    private ClaudeClient _client;
    private WebViewBridge _bridge;
    private WebViewMessageHandler _handler;
    // Session id we've already tried to auto-title, so we ask the CLI only once.
    private string _titledSessionId;
    private bool _initialized;
    // True while background agents are running (from background_tasks_changed). Gates the
    // "turn finished" attention notification so async agents don't trigger a premature one.
    private bool _hasBackgroundTasks;
    // True between the CLI's first status for a turn and its `result`. Keeps Options → Apply from
    // re-rendering the transcript mid-turn, which would drop the reply still being streamed.
    private bool _turnInFlight;

    // When set (by PaneLauncher before the pane loads), this pane opens ON this
    // session instead of a fresh one — used to land a fork in its own pane.
    private string _startupSessionId;
    // Forked-at message text to pre-fill in the composer once the fork loads.
    private string _startupPrompt;

    /// <summary>Make this pane start resumed on <paramref name="sessionId"/> rather
    /// than fresh, pre-filling the composer with <paramref name="initialPrompt"/>.
    /// Must be called before the pane's Loaded fires (PaneLauncher does this right
    /// after creating the window, like AssignPaneId).</summary>
    internal void SetStartupSession(string sessionId, string initialPrompt = null)
    {
        _startupSessionId = sessionId;
        _startupPrompt = initialPrompt;
    }

    public ChatPaneControl()
    {
        _log = OutputWindowLogger.For("chat", () => Entry?.PaneId ?? 0);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        WebView.HostKeyPressed += OnHostKeyPressed;
        // Clicking into the chat is the user answering the attention notice, so take it down.
        // Composition rendering is what makes this possible: the control lives in the WPF tree,
        // so mouse events reach us. The HwndHost version had to be told from JS instead.
        WebView.PreviewMouseDown += OnWebViewClicked;
    }

    /// <summary>The user clicked into this pane: whatever InfoBar or toast was calling them here
    /// has done its job.</summary>
    private void OnWebViewClicked(object sender, MouseButtonEventArgs e)
        => PaneAttentionService.Clear(Entry);

    /// <summary>A key <see cref="ChatWebView"/> claimed because composition rendering drops it:
    /// hand it to the page, which acts on whatever it has focused. Dropped silently before the
    /// bridge is up — there is nothing focused to act on yet.</summary>
    private void OnHostKeyPressed(Contracts.HostKeyNotification key)
        => _bridge?.Send(BridgeMessages.ToWebView.Ui.HostKey, key);

    private void OnLoaded(object sender, RoutedEventArgs e)
        => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            if (_initialized) { return; }
            _initialized = true;

            _log.Info("load: OnLoaded start");

            _bridge = new WebViewBridge(WebView, Dispatcher, _log);

            using (OutputWindowLogger.Global.PerfSpan("WebView.Init"))
            {
                await _bridge.InitAsync();
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // StatusPanel is hidden later, on the webview_ready signal (OnBridgeMessage),
            // so it covers the blank WebView until the chat has actually painted.
            SendTheme();
            // Re-push the caption: RegisterInstance runs before VS wires IVsWindowFrame, so the earlier set can no-op.
            RepushCaption();

            AgentsOptions.Applied += OnOptionsApplied;
            VSColorTheme.ThemeChanged += OnVsThemeChanged;

            // Track active editor file + selection for the context badge / <ide_*> prompt tags.
            // SubscribeToEditorEvents is owned by McpServerHost.Start — we just hook the event here.
            IdeContextService.Instance.ContextChanged += OnEditorContextChanged;

            // Workdir was resolved (solution vs home) once by PaneLauncher and lives on the entry.
            await InitAsync();
        }).FileAndForget(nameof(ChatPaneControl));

    /// <summary>Kind-specific release (the base handles _disposed guard, solution-events
    /// unadvise, and the registry drop). Unhook the theme + static Options subscriptions and
    /// dispose the client and the WebView. The IdeContextService singleton is owned by the package
    /// (McpServerHost lifetime) — only unhook our handler, don't dispose it.</summary>
    protected override void DisposeCore()
    {
        VSColorTheme.ThemeChanged -= OnVsThemeChanged;
        AgentsOptions.Applied -= OnOptionsApplied;
        WebView.HostKeyPressed -= OnHostKeyPressed;
        WebView.PreviewMouseDown -= OnWebViewClicked;
        IdeContextService.Instance.ContextChanged -= OnEditorContextChanged;
        // EnsureClient hooked this on the IdeContextService singleton; without the unhook the
        // closed pane leaks (singleton keeps it alive) and its dead _client keeps logging
        // "transport not running" on every selection change. -= is safe even if never hooked.
        IdeContextService.Instance.ContextChanged -= OnEditorContextChangedForChat;
        _handler?.Dispose();
        // Detach the client events before disposing: an event still in flight (a final
        // stdout line as the process closes) would otherwise reach this disposed control.
        if (_client != null) { DetachClientEvents(_client); }
        _client?.Dispose();
        // Last: the bridge tears down the WebView2, and anything above may still post to it.
        _bridge?.Dispose();
        _bridge = null;
    }

    private void OnEditorContextChanged(EditorContext ctx)
        // Empty FilePath = no active editor context (the WebView clears its badge on
        // falsy filePath). Strings stay non-null so the DTO's `string` type is honest.
        => _bridge?.Send(BridgeMessages.ToWebView.Ide.SelectionChanged, ctx == null
            ? new Contracts.IdeContextNotification { FilePath = "", FileName = "" }
            : new Contracts.IdeContextNotification
            {
                FilePath = ctx.FilePath ?? "",
                FileName = ctx.FileName ?? "",
                HasSelection = ctx.HasSelection,
                StartLine = ctx.StartLine,
                EndLine = ctx.EndLine,
            });

    private void OnVsThemeChanged(ThemeChangedEventArgs e) => Dispatcher.Invoke(SendTheme);

    private void SendTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _bridge?.InjectTheme(VsThemeReader.IsDark()); } catch (Exception ex) { _log.LogException("SendTheme", ex); }
    }

    /// <summary>Options → Apply. Send vs_settings (updates state.ui: font size, sticky, …)
    /// and re-render the transcript from history so the already-rendered messages/tool-rows
    /// pick up the new UI options (e.g. the "Open diff in VS" button). This only refreshes
    /// the WebView — it does NOT touch CLI state (model/mode/toggles) or respawn the CLI.</summary>
    private void OnOptionsApplied()
    {
        _bridge?.Send(BridgeMessages.ToWebView.Ui.VsSettings, WebViewBridge.BuildVsOptions());

        var sid = _client?.SessionId;
        if (string.IsNullOrEmpty(sid)) { return; }

        // Mid-turn the re-render would be destructive: the reply being streamed is not in the
        // .jsonl yet, so Cleared drops it and the page read back is the transcript as it stood
        // before the turn — the running turn disappears from the chat while the CLI is still
        // working on it. The options above are already applied; the rows rendered so far keep
        // the previous ones until the next re-render.
        if (_turnInFlight)
        {
            _log.Debug(() => $"[chat] options applied mid-turn on {sid} — settings only, transcript left alone");
            return;
        }

        // Reload the transcript into the WebView only; do NOT call ResumeSessionAsync
        // (a respawn isn't needed to re-render UI options, and triggered a crash).
        _bridge?.Send(BridgeMessages.ToWebView.Chat.Cleared, null);
        var page = Sessions.ReadHistoryRaw(sid, SessionManager.HistoryBatchSize, -1, out _);
        SendHistoryPage(page, sid);
    }

    private async Task InitAsync()
    {
        var workDir = Entry.WorkingDirectory;
        using var _ = OutputWindowLogger.Global.PerfSpan($"InitAsync({workDir})");

        // Every "New Chat" pane starts FRESH (else N panes share one conversation). Exception: a
        // forked or workspace-restored pane — _startupSessionId points at the JSONL to resume
        // (set by SetStartupSession before load). Model isn't decided here: client-first, the CLI's
        // own system/init reports it (fresh pane picks the CLI default; resume re-emits the
        // session's model) and the gate ships it to the WebView. Permission mode comes from OUR
        // Options page (the CLI doesn't restore it from --resume).
        var allowBypass = AgentsOptions.Chat.AllowDangerouslySkipPermissions;
        var permMode = PermissionMode.FromInitial(AgentsOptions.Chat.InitialPermissionMode, allowBypass);
        // Resuming (auto-resume or fork)? Read the session ONCE now so the respawn's --permission-mode
        // matches what the user last had. The transcript/title from the same read are seeded after
        // Cleared, below.
        var restoreState = !string.IsNullOrEmpty(_startupSessionId);
        SessionManager.HistoryPage resumePage = null;
        SessionInfo resumeInfo = null;
        if (restoreState)
        {
            // Off the UI thread: this reads and scans the session's .jsonl, which on a long
            // session is disk work the dispatcher shouldn't be holding.
            var sessionId = _startupSessionId;
            (permMode, resumePage, resumeInfo) = await Task.Run(() => ReadSessionState(sessionId));
        }

        // Everything below talks to the WebView and the client, so it belongs on the UI thread.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _bridge.Send(BridgeMessages.ToWebView.Chat.Cleared, null);
        _log.Info($"load: InitAsync sessionId={_startupSessionId ?? "(none)"} (mode={permMode})");

        // Before the history, never after: those rows shorten their paths against the working
        // directory, and a row drawn without one keeps the absolute path it was born with. This
        // is why ui_init no longer waits for the CLI — permMode below is what we pass via
        // --permission-mode (the CLI doesn't report it), and the rest of the CLI's state follows
        // on cli_state when StartupAsync has gathered it, seconds later, enabling the toolbar.
        // Seed the resumed session's transcript + title (after Cleared, from the read above).
        // SendHistoryPage → LoadPromptHistory read the entry's constant workdir, so this works even
        // though _client doesn't exist yet on the resume-path.
        SendUiInit();
        if (restoreState)
        {
            SendHistoryPage(resumePage, _startupSessionId);
            SetSessionTitle(resumeInfo?.CustomTitle ?? resumeInfo?.AiTitle ?? resumeInfo?.LastPrompt);
        }

        // claude.exe must be installed (native / WinGet / npm). Missing → show the same "not
        // installed" panel as the CLI pane instead of throwing when the transport spawns a null exe.
        if (ClaudeInstall.ResolveExecutable() == null)
        {
            _log.Warn("[chat] claude.exe not found — showing 'not installed' panel");
            Content = ClaudeInstall.BuildMissingPanel();
            return;
        }

        EnsureClient();
        // Start the MCP server and pass its port (via CLAUDE_CODE_SSE_PORT) so this chat's claude
        // connects to THIS VS's server directly. Idempotent: returns the running port if already up.
        var ssePort = Mcp.McpServerHost.Instance.EnsureStarted();
        // Start the CLI now (not lazily on first prompt) so its `initialize` runs
        // and the model catalogue / slash commands reach the UI on open, like VS Code.
        await _client.StartAsync(new ClientOptions
        {
            WorkingDirectory = workDir,
            // Fork: we already wrote the <newId>.jsonl on disk, so --resume loads it.
            // (--session-id would try to CREATE that id and the CLI rejects it as
            // "already in use".) A fresh pane passes neither.
            ResumeSessionId = _startupSessionId,
            InitialPermissionMode = permMode,
            AllowBypassPermissions = allowBypass,
            SsePort = ssePort,
            Env = Entry.Profile.Env,
        });

        // Forked pane: drop the forked-at message into the composer for editing/resend.
        // (Transcript + title were already seeded above by ApplyResumedSession.)
        if (restoreState && !string.IsNullOrEmpty(_startupPrompt))
        {
            _bridge.Send(BridgeMessages.ToWebView.Ui.SetComposer, new Contracts.SetComposerNotification { Text = _startupPrompt });
        }
    }

    /// <summary>Creates the single ClaudeClient instance on demand (once per tool window lifetime).</summary>
    private void EnsureClient()
    {
        if (_client != null) { return; }

        _client = new ClaudeClient(_log)
        {
            // IDE tools exposed as in-process SDK MCP server (mcp_set_servers after init).
            // Name must be "vs", NOT "ide" — the CLI reserves "ide" for its own internal
            // integration and does not surface those tools to the model; a custom name
            // makes all our tools appear as mcp__vs__* (openFile, getCurrentSelection, …).
            SdkMcpServerName = "vs",
            McpMessageHandler = json => Mcp.McpServerHost.Instance.ServeMcpMessageAsync(json)
        };
        AttachClientEvents(_client);
        // Unhook first, like MessageReceived below: the subscription lives on a singleton that
        // outlives the client, so a second pass here would send every selection change twice.
        IdeContextService.Instance.ContextChanged -= OnEditorContextChangedForChat;
        IdeContextService.Instance.ContextChanged += OnEditorContextChangedForChat;

        _handler = new WebViewMessageHandler(_bridge, _client, Entry, _log);
        _bridge.MessageReceived -= OnBridgeMessage;
        _bridge.MessageReceived += OnBridgeMessage;
    }

    private void OnBridgeMessage(string type, JObject data, int? id)
    {
        switch (type)
        {
            // App painted its first frame: hide the native "Initializing…"
            // placeholder (it covered the blank WebView until now) and mark the
            // pane ready so the toolbar enables New session / History.
            case BridgeMessages.FromWebView.Ui.Ready:
                StatusPanel.Visibility = Visibility.Collapsed;
                SetReady(true);
                // First open: focus the composer now, not earlier. A ui_focus_input sent during
                // startup lands before the bundle has mounted cv-prompt, so the textarea it looks
                // for isn't there yet and the call is a no-op — which is why the pane opened
                // needing a click. Later activations work because they come from a frame change,
                // long after this. Only on the pane that VS considers active, so opening a second
                // chat in the background doesn't steal focus from the one being used.
                if (Pane?.IsActiveFrame() == true) { FocusInput(); }
                // Name this pane's page after the pane. The browser's task manager labels each row
                // with the document title, and every chat ships the same index.html — so without
                // this they all read "cv4vs Agents" and none of them says which pane it is.
                _bridge?.SetDocumentTitle(Entry?.Title);
                // Seed the IDE-context badge with the already-open editor: we only subscribe to
                // future ContextChanged events, so without this the badge stays empty until the
                // first editor click. Force a snapshot emit now that the WebView can receive it.
                IdeContextService.Instance.ForceEmitCurrentContext();
                break;

            // Everything else is chat protocol — hand it to the message handler.
            default:
                _handler?.Handle(type, data, id);
                break;
        }
    }

    private void OnEditorContextChangedForChat(EditorContext ctx)
    {
        if (_client == null)
        {
            _log.Trace(() => "[ChatSelection] skip: _client null");
            return;
        }
        // Eye closed for this session (the composer's IDE-context badge): don't leak the editor
        // selection into the chat. Send an empty selection so the CLI drops any cached one.
        if (ctx == null || Entry?.Options.SendSelection == false)
        {
            _log.Trace(() => "[ChatSelection] no context / eye off → empty selection");
            _client.SendSelectionChanged(string.Empty, null, null, 0, 0, 0, 0, isEmpty: true);
        }
        else
        {
            _log.Trace(() => $"[ChatSelection] file={ctx.FilePath} sel={ctx.HasSelection} lines={ctx.StartLine}-{ctx.EndLine} running={_client.IsRunning}");
            _client.SendSelectionChanged(ctx.SelectedText ?? string.Empty,
                                         ctx.FilePath,
                                         PathHelpers.ToFileUri(ctx.FilePath),
                                         Math.Max(0, ctx.StartLine - 1),
                                         Math.Max(0, ctx.StartColumn),
                                         Math.Max(0, ctx.EndLine - 1),
                                         Math.Max(0, ctx.EndColumn),
                                         !ctx.HasSelection);
        }
    }
}

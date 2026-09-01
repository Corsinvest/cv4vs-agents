/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// Concrete <see cref="IClaudeClient"/> implementation driving claude.exe through NDJSON
/// stream-json on stdin/stdout and the bidirectional control protocol.
/// </summary>
internal sealed partial class ClaudeClient : IClaudeClient
{
    // Built in the constructor, not here: a field initializer runs before _log is assigned, so the
    // first transport would lose the pane tag. Volatile because StartProcess swaps it on respawn
    // while MCP worker threads are reading it to write — without it they can go on addressing the
    // disposed instance. Readers that then USE it must copy it to a local first: the field can
    // change between two reads of it.
    private volatile NdjsonTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new();
    private int _requestCounter;

    // Track the control request_id associated to a tool_use_id so we can respond later.
    private readonly ConcurrentDictionary<string, string> _toolRequestIds = new();

    // Last options used — replayed on auto-restart when the process dies.
    private ClientOptions _lastOptions;

    // Profile env from the original StartAsync/Prepare — preserved across every respawn
    // (NewSession/Resume/WorkingDirectoryChange/auto-restart) the same way Model/SsePort
    // are, so a profiled pane never silently reverts to native Claude.
    private IReadOnlyDictionary<string, string> _env;

    public string WorkingDirectory { get; private set; }
    public string SessionId { get; private set; }

    /// <summary>Whether THIS process was launched with file checkpointing on — not what the option
    /// says now. The CLI reads it from the environment at startup, so changing the option leaves a
    /// running session as it was, and the UI has to follow the process rather than the setting.</summary>
    public bool FileCheckpoints => _lastOptions?.FileCheckpoints ?? false;
    public string Model { get; private set; }
    public string PermissionMode { get; private set; } = Client.PermissionMode.Default;
    public int? BridgeEpoch { get; private set; }
    public AccountInfo Account { get; private set; }
    public bool IsRunning => _transport.IsRunning;

    /// <summary>PID of the claude.exe this client drives, or -1 when no process is running. For the
    /// info dialog and bug reports: with several panes open, "which claude.exe is this one" is
    /// otherwise only answerable by matching command lines.</summary>
    public int Pid => _transport.Pid;

    public event EventHandler<InitializedEventArgs> Initialized;
    /// <summary>The CLI startup state (model + toggles from initialize+get_settings), gathered
    /// without a user turn so the UI can seed on open. Fired once per StartProcess (open + respawn).</summary>
    public event EventHandler<CliStateReceivedEventArgs> CliStateReceived;
    public event EventHandler<ModelsReceivedEventArgs> ModelsReceived;
    public event EventHandler<AssistantMessageEventArgs> AssistantMessageReceived;
    public event EventHandler<UserMessageEventArgs> UserMessageReceived;
    public event EventHandler<ResultEventArgs> ResultReceived;
    public event EventHandler<ToolPermissionRequestEventArgs> ToolPermissionRequested;
    /// <summary>The CLI cancelled a pending can_use_tool (interrupt / superseded turn) — the
    /// permission banner for that tool_use must be dismissed.</summary>
    public event EventHandler<ToolPermissionCancelledEventArgs> ToolPermissionCancelled;
    public event EventHandler<HookCallbackEventArgs> HookCallbackRequested;
    public event EventHandler<RateLimitEventArgs> RateLimitReceived;
    /// <summary>Remote Control bridge changed state. Only `failed` is actionable.</summary>
    public event EventHandler<BridgeStateEventArgs> BridgeStateChanged;
    public event EventHandler<AssistantTextDeltaEventArgs> AssistantTextDelta;
    public event EventHandler<AssistantThinkingDeltaEventArgs> AssistantThinkingDelta;
    public event EventHandler<ToolProgressEventArgs> ToolProgressReceived;
    public event EventHandler<JObject> SystemMessageReceived;
    public event EventHandler<string> SessionIdChanged;
    public event EventHandler<string> PermissionModeChanged;
    /// <summary>The CLI reset the conversation (/clear). Arg = new_conversation_id.
    /// A fresh system/init with the new session_id follows.</summary>
    public event EventHandler<string> ConversationReset;
    public event EventHandler<string> Error;
    public event EventHandler<ProcessStartedEventArgs> ProcessStarted;
    public event EventHandler<ProcessExitedEventArgs> ProcessExited;

    /// <summary>Serves an inbound MCP JSON-RPC message (from the CLI's `mcp_message`
    /// control request) and returns the JSON-RPC response string. Set by the host to
    /// the in-process MCP dispatcher; null = no SDK MCP server registered. This is how
    /// the stream-json chat exposes IDE tools (the CLI pane uses the WebSocket server
    /// instead). Input/output are JSON-RPC strings the dispatcher already handles.</summary>
    public Func<string, Task<string>> McpMessageHandler { get; set; }

    /// <summary>Name of the in-process SDK MCP server registered via mcp_set_servers
    /// after init (tools surface as `mcp__&lt;name&gt;__*`). Null = don't register.</summary>
    public string SdkMcpServerName { get; set; }

    // Handlers kept as fields so we can detach cleanly when rotating the transport.
    private EventHandler<JObject> _onLine;
    private EventHandler<string> _onError;
    private EventHandler<(int exitCode, bool intentional)> _onExited;

    private readonly OutputWindowLogger _log;

    // Optional: the context/usage probes build a client with no pane behind it (→ Global, unprefixed).
    public ClaudeClient(OutputWindowLogger log = null)
    {
        _log = log ?? OutputWindowLogger.Global;
        _transport = new NdjsonTransport(_log);
        AttachTransportEvents();
    }

    private void AttachTransportEvents()
    {
        _onLine = (_, obj) => HandleLine(obj);
        _onError = (_, msg) => Error?.Invoke(this, msg);
        _onExited = (_, t) =>
        {
            // The process is gone: fail in-flight control_requests and drop tracked
            // tool requests, so nothing hangs until its timeout or answers a dead CLI.
            RejectPendingRequests("CLI process exited");
            ProcessExited?.Invoke(this, new ProcessExitedEventArgs { ExitCode = t.exitCode, Intentional = t.intentional });
        };
        _transport.LineReceived += _onLine;
        _transport.ErrorLine += _onError;
        _transport.Exited += _onExited;
    }

    private void DetachTransportEvents()
    {
        if (_transport == null) { return; }
        if (_onLine != null) { _transport.LineReceived -= _onLine; }
        if (_onError != null) { _transport.ErrorLine -= _onError; }
        if (_onExited != null) { _transport.Exited -= _onExited; }
    }

    /// <summary>
    /// Saves the options that will be used on the next start. Does NOT launch the process.
    /// Useful at boot / on solution change to keep state in sync while the CLI is lazily started on first prompt.
    /// </summary>
    public void Prepare(ClientOptions options)
    {
        if (options == null) { throw new ArgumentNullException(nameof(options)); }
        if (string.IsNullOrEmpty(options.WorkingDirectory) || !Directory.Exists(options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {options.WorkingDirectory}");
        }

        // If the process is currently running and the workdir changed, kill it — the next
        // send will respawn with the new options. Model/permission-mode changes don't need this.
        if (_transport.IsRunning && !string.Equals(WorkingDirectory, options.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            KillForRespawn();
        }

        _lastOptions = options;
        _env = options.Env;
        WorkingDirectory = options.WorkingDirectory;
        SessionId = options.ResumeSessionId;
        PermissionMode = options.InitialPermissionMode ?? Client.PermissionMode.Default;
    }

    public Task StartAsync(ClientOptions options)
    {
        Prepare(options);
        StartProcess(options);
        return Task.CompletedTask;
    }

    private void StartProcess(ClientOptions options)
    {
        if (_transport.IsRunning) { return; }

        // Transport instances are not reusable after Dispose — rotate to a fresh one and
        // detach listeners from the old one so its late Exited event doesn't bubble up.
        DetachTransportEvents();
        try { _transport.Dispose(); } catch { }
        _transport = new NdjsonTransport(_log);
        AttachTransportEvents();
        // A new process has no bridge: the CLI reports nothing about Remote Control after
        // --resume, so nothing else would clear this.
        BridgeEpoch = null;

        // Launch-only args. --include-partial-messages enables stream_event + tool_progress.
        // NOTE: no --ide here. In stream-json mode the CLI's --ide auto-connect is
        // UI-only (REPL hook) and never runs, so it does nothing. Instead we expose
        // the IDE tools as an in-process SDK MCP server: declared in the initialize
        // payload (sdkMcpServers) and registered via mcp_set_servers. The interactive
        // CLI pane keeps --ide + WS lockfile.
        var args = "--output-format stream-json --verbose --input-format stream-json --include-partial-messages";
        // --setting-sources: headless mode loads NO settings by default; re-enable so the user's
        // ~/.claude/settings.json permissions.allow/deny apply (else CLI asks can_use_tool for every tool).
        args += " --setting-sources user,project,local";
        // Auto-approve our in-process IDE MCP tools so Claude can call them without a
        // permission prompt (acceptEdits does NOT auto-approve MCP tools — per SDK docs).
        if (!string.IsNullOrEmpty(SdkMcpServerName))
        {
            args += $" --allowedTools mcp__{SdkMcpServerName}__*";
        }
        // Model is NOT passed via --model: we launch without one and set it after init with a
        // `set_model` control_request (InitializeAndPublishCatalogAsync), so a model change is a
        // hot-swap on the live process instead of a respawn.
        if (!string.IsNullOrEmpty(options.ResumeSessionId)) { args += " --resume " + options.ResumeSessionId; }

        // Grants the capability to switch into bypassPermissions later; weakens nothing now.
        // Must be the allow- form: --dangerously-skip-permissions disarms permissions immediately,
        // whatever --permission-mode says. Without either, the CLI refuses the swap outright.
        if (options.AllowBypassPermissions) { args += " --allow-dangerously-skip-permissions"; }

        // The mode decides WHICH tools need confirmation, the prompt-tool is the channel to ask.
        var mode = options.InitialPermissionMode ?? Client.PermissionMode.Default;
        if (mode == Client.PermissionMode.AcceptEdits) { args += " --permission-mode acceptEdits"; }
        else if (mode == Client.PermissionMode.Plan) { args += " --permission-mode plan"; }
        else if (mode == Client.PermissionMode.Auto) { args += " --permission-mode auto"; }
        // Needed on resume: a session left in bypass would otherwise restart in `default`.
        else if (mode == Client.PermissionMode.BypassPermissions) { args += " --permission-mode bypassPermissions"; }

        // ALWAYS, whatever the mode. Verified on CLI 2.1.220: without this flag the CLI drops
        // AskUserQuestion from the session entirely — the turn ends `success` and the question is
        // never emitted, on either channel. So the flag doesn't merely carry permission prompts,
        // it registers the interactive tool: a session in bypass could otherwise not ask anything.
        args += " --permission-prompt-tool stdio";

        // Profile env goes FIRST, our required keys LAST — a profile (e.g. z.ai/GLM base
        // URL + token) must never be able to override what makes the IDE integration work.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.Env != null) { foreach (var kv in options.Env) { env[kv.Key] = kv.Value; } }
        // CLAUDE_CODE_ENTRYPOINT=claude-vscode tells the CLI it runs inside an IDE
        // extension, so its `initialize` returns the FULL model catalogue — including
        // unavailable_models (e.g. Fable, shown greyed in the picker). Without it the
        // CLI replies in headless mode and omits the disabled models. (Verified with
        // tools/cli-probe: this env var alone flips unavailable_models on.)
        env["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode";
        // Without this the CLI takes no file snapshots at all on our path, so there is nothing to
        // rewind to. It keeps file history unconditionally when it runs its own REPL, but a
        // stream-json session is "non-interactive" to it and there the feature is opt-in through
        // this variable — which is how the VS Code extension gets it too: it passes
        // `enableFileCheckpointing: true` to the Agent SDK, and the SDK sets exactly this.
        // NOT forced past a user who turned checkpointing off: the CLI reads this one only while
        // CLAUDE_CODE_DISABLE_FILE_CHECKPOINTING is unset, and that is the right way round. Whoever
        // set the disable meant it, and the cost of ignoring them is copies of their files on disk.
        // Off leaves the variable unset rather than setting it false — that is what "not asked for"
        // looks like to the CLI, and it keeps the profile's own env the only other voice.
        if (options.FileCheckpoints) { env["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true"; }
        _transport.Start(ClaudeInstall.ResolveExecutable(), args, options.WorkingDirectory, env,
                         ClaudeInstall.InheritedSessionEnvVars);

        ProcessStarted?.Invoke(this, new ProcessStartedEventArgs
        {
            Pid = _transport.Pid,
            WorkingDirectory = options.WorkingDirectory,
            SessionId = options.ResumeSessionId,
        });

        // Register a PreToolUse hook for Edit|Write|Read so the host can save a
        // dirty file before Claude touches it (the "autosave" feature). The hook
        // is harmless if the feature is off — the host's HookCallback handler
        // decides whether to actually save. Best-effort: if the CLI rejects the
        // initialize, autosave simply won't fire (no crash, CLI keeps running).
        SendInitializeHooks();
        // The SDK MCP server names are declared in the initialize payload (sdkMcpServers) AND
        // registered via mcp_set_servers; the CLI needs both before it will route tool calls.
        RegisterSdkMcpServer();
    }

    /// <summary>Register the in-process SDK MCP server so the stream-json chat can
    /// call our IDE tools (mcp__&lt;name&gt;__*). Sent as `mcp_set_servers` with a
    /// `type:"sdk"` server; the CLI then calls tools back over `mcp_message`
    /// (handled by HandleMcpMessage). No-op when unset.</summary>
    private void RegisterSdkMcpServer()
    {
        var name = SdkMcpServerName;
        if (string.IsNullOrEmpty(name) || McpMessageHandler == null) { return; }
        _ = RegisterSdkMcpServerAsync(name);
    }

    private async Task RegisterSdkMcpServerAsync(string name)
    {
        try
        {
            var resp = await SendControlRequestAsync(ClientMessages.ControlSubtype.McpSetServers, new
            {
                servers = new Dictionary<string, object>
                {
                    [name] = new { type = "sdk", name },
                },
            });
            // errors maps failed-server name → reason. A non-empty entry means our IDE tools
            // won't reach the CLI (mcp__<name>__* calls fail) — surface it instead of a silent drop.
            if (resp?["errors"] is JObject errors && errors.HasValues)
            {
                _log.Warn($"[client] SDK MCP server '{name}' registration failed: {JsonExtensions.ToIndentedString(errors)}");
            }
        }
        catch (Exception ex)
        {
            _log.LogException("ClaudeClient.RegisterSdkMcpServer", ex);
        }
    }

    /// <summary>Callback id the CLI sends back on the PreToolUse hook.</summary>
    public const string AutosaveHookId = "cv_autosave";

    /// <summary>Callback ids the CLI sends back on the diagnostics hooks (pre = baseline, post = check).</summary>
    public const string DiagBaselineHookId = "cv_diag_baseline";
    public const string DiagCheckHookId = "cv_diag_check";

    private void SendInitializeHooks() =>
        // `initialize` carries the model catalogue + rich slash commands (→ ModelsReceived) and the
        // fast-mode state; get_settings adds the model + Model-menu toggles. Together they seed the UI
        // WITHOUT a user turn — system/init only arrives on the first turn, too late to enable the
        // toolbar. Fired on every StartProcess (open + respawn).
        _ = StartupAsync();

    private async Task StartupAsync()
    {
        try
        {
            // Capture the permission mode for THIS startup up front — before the awaits below let a
            // rapid respawn reassign the field (the CLI never reports permissionMode; it's ours).
            var permissionMode = PermissionMode;
            // Declare SDK MCP servers inside `initialize` (not via mcp_set_servers after init) —
            // the JS SDK's flow; the CLI uses this to build the full reply (incl. unavailable_models).
            var sdkServers = !string.IsNullOrEmpty(SdkMcpServerName) && McpMessageHandler != null
                ? new[] { SdkMcpServerName }
                : null;

            var resp = await SendControlRequestAsync(ClientMessages.ControlSubtype.Initialize, new
            {
                hooks = new
                {
                    PreToolUse = new[]
                    {
                        new { matcher = "Edit|Write|Read", hookCallbackIds = new[] { AutosaveHookId } },
                        new { matcher = "Edit|Write|MultiEdit", hookCallbackIds = new[] { DiagBaselineHookId } },
                    },
                    PostToolUse = new[]
                    {
                        new { matcher = "Edit|Write|MultiEdit", hookCallbackIds = new[] { DiagCheckHookId } },
                    },
                },
                sdkMcpServers = sdkServers,
            });
            if (resp == null) { return; }
            var models = resp["models"] as JArray;
            var unavailable = resp["unavailable_models"] as JArray;
            var commands = resp["commands"] as JArray;
            if (resp["account"] is JObject acct)
            {
                Account = new AccountInfo
                {
                    Email = acct.Val("email"),
                    Organization = acct.Val("organization"),
                    SubscriptionType = acct.Val("subscriptionType"),
                    ApiProvider = acct.Val("apiProvider"),
                };
            }
            // Gate on the raw arrays, not on the parsed lists: ParseModels flattens "absent" and
            // "empty" to the same [], and an absent catalogue must not fire the event.
            if (models != null || unavailable != null || commands != null)
            {
                ModelsReceived?.Invoke(this, new ModelsReceivedEventArgs
                {
                    Models = ParseModels(models),
                    UnavailableModels = ParseModels(unavailable),
                    Commands = ParseCommands(commands),
                });
            }
            // fast_mode_state is present in the initialize reply only when fast mode is available for
            // the account/org (else absent → "off"). It's the ONLY startup field the CLI won't give
            // via get_settings.
            var fastModeState = resp.Val("fast_mode_state", "off");

            // get_settings gives the model + the Model-menu toggles without a turn (applied.* are the
            // runtime-resolved values that actually go to the API; effective.* is the disk merge).
            var settings = await GetSettingsAsync();
            var eff = settings?["effective"] as JObject;
            var applied = settings?["applied"] as JObject;
            Model = applied?.Val("model") ?? Model;   // keep the class field in sync for later use
            CliStateReceived?.Invoke(this, new CliStateReceivedEventArgs
            {
                Model = applied?.Val("model"),
                PermissionMode = permissionMode,
                EffortLevel = applied?.Val("effort") ?? eff?.Val("effortLevel"),
                AlwaysThinkingEnabled = eff?.ValBool("alwaysThinkingEnabled"),
                Ultracode = applied?.ValBool("ultracode"),
                SwitchModelsOnFlag = eff?.ValBool("switchModelsOnFlag"),
                BypassPermissionsDisabled =
                    eff?["permissions"].Val("disableBypassPermissionsMode") == "disable",
                SpinnerVerbs = ParseSpinnerVerbs(eff?["spinnerVerbs"] as JObject),
                FastModeState = fastModeState,
            });
        }
        catch (Exception ex)
        {
            _log.LogException("ClaudeClient.StartupAsync", ex);
        }
    }

    /// <summary>Ensures the process is alive, restarting with the last known options (resuming current session) if it died.</summary>
    private void EnsureRunning()
    {
        if (_transport.IsRunning) { return; }
        if (_lastOptions == null)
        {
            _log.Warn("[client] SendPrompt before Prepare/StartAsync — transport not running, prompt dropped");
            return;
        }

        var replay = new ClientOptions
        {
            WorkingDirectory = WorkingDirectory ?? _lastOptions.WorkingDirectory,
            ResumeSessionId = SessionId ?? _lastOptions.ResumeSessionId,
            InitialPermissionMode = PermissionMode ?? _lastOptions.InitialPermissionMode,
            AllowBypassPermissions = _lastOptions.AllowBypassPermissions,
            FileCheckpoints = _lastOptions.FileCheckpoints,
            SsePort = _lastOptions.SsePort,   // keep talking to the same MCP server after a restart
            // Keep the profile's provider across respawns — else the pane silently reverts to native Claude.
            Env = _env ?? _lastOptions?.Env,
        };
        _log.Info($"=== auto-restart workdir={replay.WorkingDirectory} session={replay.ResumeSessionId ?? "(none)"}");
        StartProcess(replay);
    }

    public Task StopAsync()
    {
        _transport.DisposeIntentional();
        return Task.CompletedTask;
    }

    /// <summary>Starts a new empty session in the same working directory. Kills the current process and spawns a fresh one.
    /// Logs its own failure rather than leaving it to the caller: the pane starts this and drops the
    /// Task, and by then the transcript is already cleared — a silent failure leaves an empty pane
    /// with no process behind it, which reads as "still loading" instead of as an error.</summary>
    public async Task NewSessionAsync()
    {
        SessionId = null;
        KillForRespawn();
        try
        {
            await StartAsync(new ClientOptions
            {
                WorkingDirectory = WorkingDirectory,
                InitialPermissionMode = PermissionMode,
                // Preserved across respawn like Env: losing it would make bypass unreachable
                // for the rest of the pane's life, with nothing to explain why.
                AllowBypassPermissions = _lastOptions?.AllowBypassPermissions ?? false,
                // Preserved for the same reason: it is read from the environment at launch, so a
                // respawn that dropped it would quietly stop taking snapshots mid-session.
                FileCheckpoints = _lastOptions?.FileCheckpoints ?? true,
                Env = _env,
            });
        }
        catch (Exception ex)
        {
            _log.LogException("[client] new_session", ex);
            throw;
        }
    }

    /// <summary>Resumes an existing session by id. Requires respawn. The
    /// caller passes the session's own mode (read from its JSONL) so the
    /// respawned CLI runs on the SAME mode shown in the selector — not
    /// whatever the client happened to hold. Null falls back to the current.
    /// Model is not passed: the CLI's init re-emits the session's own model on --resume.
    /// Logs its own failure, like NewSessionAsync — and here it matters more: the caller has already
    /// pushed the history into the WebView, so a silent failure shows the right transcript over a
    /// process that isn't there, and looks like it worked until the first prompt goes nowhere.</summary>
    public async Task ResumeSessionAsync(string sessionId, string permissionMode = null)
    {
        KillForRespawn();
        PermissionMode = permissionMode ?? PermissionMode;
        try
        {
            await StartAsync(new ClientOptions
            {
                WorkingDirectory = WorkingDirectory,
                ResumeSessionId = sessionId,
                InitialPermissionMode = PermissionMode,
                AllowBypassPermissions = _lastOptions?.AllowBypassPermissions ?? false,
                // Preserved for the same reason: it is read from the environment at launch, so a
                // respawn that dropped it would quietly stop taking snapshots mid-session.
                FileCheckpoints = _lastOptions?.FileCheckpoints ?? true,
                Env = _env,
            });
        }
        catch (Exception ex)
        {
            _log.LogException($"[client] resume_session {sessionId}", ex);
            throw;
        }
    }

    private void KillForRespawn() => _transport.DisposeIntentional();

    // Hot-swap operations — these must never respawn the process.

    /// <summary>Logs its own failure like the rest, so no caller has to wonder whether this one
    /// reports for itself. The property advances only after the ack, which is what lets the caller
    /// echo the real value back and roll an optimistic selector onto it — that echo has to stay at
    /// the call site, where the bridge is.</summary>
    public async Task SetModelAsync(string model)
    {
        try
        {
            await SendControlRequestAsync(ClientMessages.ControlSubtype.SetModel, new { model });
        }
        catch (Exception ex)
        {
            _log.LogException($"[client] set_model {model ?? "(default)"}", ex);
            throw;
        }
        Model = model;
    }

    /// <summary>Same as SetModelAsync — and the echo matters more here: a selector left reading
    /// "Plan" while the CLI is still in bypass is the one lie that costs files.</summary>
    public async Task SetPermissionModeAsync(string mode)
    {
        try
        {
            await SendControlRequestAsync(ClientMessages.ControlSubtype.SetPermissionMode, new { mode });
        }
        catch (Exception ex)
        {
            _log.LogException($"[client] set_permission_mode {mode}", ex);
            throw;
        }
        PermissionMode = mode;
    }

    /// <summary>Turn Remote Control on or off on the live session. Returns the claude.ai URL when
    /// enabling, null when disabling. A `success` carrying no session_url is treated as a failure:
    /// the CLI answers exactly that when the payload field is misspelled, and a silent no-op would
    /// leave the UI claiming a connection that isn't there.</summary>
    public async Task<string> SetRemoteControlAsync(bool enabled)
    {
        try
        {
            var resp = await SendControlRequestAsync(
                ClientMessages.ControlSubtype.RemoteControl, new { enabled });
            // Whole payload: we map session_url only, and bridge_epoch is the one field that
            // could tell a reconnect from a fresh bridge — see the bridge_state log.
            _log.Debug(() => $"[client] remote_control enabled={enabled} → {resp.ToIndentedString()}");
            // Identifies the bridge this call created, so a late `failed` from an earlier one
            // can be told apart. Cleared on disable: no bridge, nothing to match.
            BridgeEpoch = enabled ? (int?)resp["bridge_epoch"] : null;
            if (!enabled) { return null; }
            var url = resp.Val("session_url");
            if (string.IsNullOrEmpty(url))
            {
                throw new InvalidOperationException("The CLI accepted the request but returned no session URL.");
            }
            return url;
        }
        catch (Exception ex)
        {
            _log.LogException($"[client] remote_control enabled={enabled}", ex);
            throw;
        }
    }

    /// <summary>Logs its own failure: the WebView frees itself the moment it asks (it can't wait on
    /// a wedged CLI), so a failed interrupt is invisible from the UI — it reads as stopped while the
    /// turn runs on. Callers fire and forget, and the 10s request timeout would fault this into
    /// silence, so the log is the only place the divergence can surface.</summary>
    public async Task InterruptAsync()
    {
        try
        {
            await SendControlRequestAsync(ClientMessages.ControlSubtype.Interrupt, null);
        }
        catch (Exception ex)
        {
            _log.LogException("[client] interrupt", ex);
            throw;
        }
    }

    /// <summary>Structured /usage data: session cost + claude.ai plan rate-limit
    /// windows. Experimental in the SDK (shape may change) — returned raw so the
    /// webview can render defensively. Null on error.</summary>
    public async Task<JObject> GetUsageAsync()
    {
        try
        {
            return await SendControlRequestAsync(ClientMessages.ControlSubtype.GetUsage, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Fetches the current session's context-window breakdown (get_context_usage).
    /// Returned raw; the handler maps it into the typed ContextUsage DTOs. Null on error.</summary>
    public async Task<JObject> GetContextUsageAsync()
    {
        try
        {
            return await SendControlRequestAsync(ClientMessages.ControlSubtype.GetContextUsage, null);
        }
        catch
        {
            return null;
        }
    }

    public Task ApplyFlagSettingsAsync(object settings)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.ApplyFlagSettings, new { settings });

    /// <summary>Enable/disable extended thinking at runtime. ON = budget 31999 + summarized display;
    /// OFF = budget 0. display is omitted when null so the CLI keeps the session mode.
    /// <para>Logs its own failure, like the other fire-and-forget hot-swaps: the caller drops the
    /// Task, and unlike the model and permission selectors there is no echo back to roll the UI
    /// onto what the CLI really holds — the toggle would simply keep showing a setting that never
    /// took.</para></summary>
    public async Task SetMaxThinkingTokensAsync(int maxThinkingTokens, string display)
    {
        try
        {
            await SendControlRequestAsync(
                ClientMessages.ControlSubtype.SetMaxThinkingTokens,
                display == null
                    ? (object)new { max_thinking_tokens = maxThinkingTokens }
                    : new { max_thinking_tokens = maxThinkingTokens, thinking_display = display });
        }
        catch (Exception ex)
        {
            _log.LogException("[client] set_max_thinking_tokens", ex);
            throw;
        }
    }

    public async Task<JObject> GetSettingsAsync()
    {
        try { return await SendControlRequestAsync(ClientMessages.ControlSubtype.GetSettings, null); }
        catch { return null; }
    }

    /// <summary>Restore the files to the CLI's snapshot taken before <paramref name="userMessageId"/>.
    /// <para>With <paramref name="dryRun"/> nothing is written: the CLI answers whether it *could*
    /// rewind to that message, and with what — <c>canRewind</c>, plus <c>filesChanged</c>,
    /// <c>insertions</c> and <c>deletions</c>. That is the only way to know whether a checkpoint
    /// exists for a message, so it is what a UI asks before offering the action.</para>
    /// <para>The response is returned rather than dropped: an error here ("File rewinding is not
    /// enabled", "No file checkpoint found for this message") is the answer, not a failure.</para></summary>
    public async Task<JObject> RewindFilesAsync(string userMessageId, bool dryRun)
    {
        try
        {
            return await SendControlRequestAsync(
                ClientMessages.ControlSubtype.RewindFiles,
                new { user_message_id = userMessageId, dry_run = dryRun });
        }
        catch (Exception ex)
        {
            // A refusal comes back as an error response, and "no checkpoint here" is a normal
            // answer for a probe — log it and let the caller read canRewind=false.
            _log.Warn($"[client] rewind_files failed (uuid={userMessageId}, dryRun={dryRun}): {ex.Message}");
            return null;
        }
    }

    public Task StopTaskAsync(string taskId)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.StopTask, new { task_id = taskId });

    /// <summary>Detach a running task from the turn: the blocking tool call returns at once and the
    /// turn carries on, while the task keeps going and reports its end as usual.
    /// <para>Keyed by tool_use_id, not task_id — that is what the CLI takes here. Omitting it
    /// detaches every foreground task, which is what Ctrl+B does in the terminal.</para>
    /// <para>One-way: there is no request that brings a task back into the turn.</para></summary>
    public Task DetachTaskAsync(string toolUseId = null)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.DetachTask,
                                   toolUseId == null ? null : new { tool_use_id = toolUseId });

    /// <summary>
    /// Asks the CLI to generate a short AI title for the current session, based on <paramref name="description"/>
    /// (typically the first user prompt). If <paramref name="persist"/> is true the CLI writes the title to the JSONL itself;
    /// otherwise the caller is responsible for persisting it.
    /// </summary>
    public async Task<string> GenerateSessionTitleAsync(string description, bool persist = false)
    {
        var resp = await SendControlRequestAsync(ClientMessages.ControlSubtype.GenerateSessionTitle, new
        {
            description,
            persist,
        });
        return resp.Val("title");
    }

    /// <summary>
    /// Sets the session's user-facing title through the live CLI, which persists it to the JSONL
    /// itself (a <c>custom-title</c> entry — the same shape <see cref="SessionManager.Rename"/>
    /// writes). Going through the CLI keeps it the single writer of a file it holds open, and
    /// leaves its in-memory title in step with what is on disk.
    /// Returns false when the CLI rejects the request — a version that predates the subtype —
    /// so the caller can fall back to writing the file directly.
    /// </summary>
    public async Task<bool> RenameSessionAsync(string title)
    {
        try
        {
            await SendControlRequestAsync(ClientMessages.ControlSubtype.RenameSession, new { title });
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"[client] rename_session refused ({ex.Message}) — falling back to the JSONL");
            return false;
        }
    }

    public async Task<McpStatus> GetMcpStatusAsync()
    {
        var resp = await SendControlRequestAsync(ClientMessages.ControlSubtype.McpStatus, null);
        var status = new McpStatus();
        if (resp?["mcpServers"] is JArray arr)
        {
            foreach (var s in arr)
            {
                status.Servers.Add(new McpServerStatus
                {
                    Name = s.Val("name", ""),
                    Status = s.Val("status", ""),
                    Error = s.Val("error"),
                    Scope = s.Val("scope"),
                });
            }
        }
        return status;
    }

    /// <summary>Asks the live CLI for the model catalogue — the same list `initialize` seeds us with
    /// (the CLI builds both from getModelOptions), but askable at any time, which `initialize` is not:
    /// the catalogue follows the account/provider/settings cascade and the CLI never pushes a change.
    /// Empty when the CLI predates the subtype or answers without models.
    /// Deliberately does NOT raise ModelsReceived: that path publishes only on the first init
    /// (see ChatPaneControl.OnModelsReceived), so an event here would be silently dropped.</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync()
    {
        try
        {
            var resp = await SendControlRequestAsync(ClientMessages.ControlSubtype.ListModels, null);
            return ParseModels(resp?["models"] as JArray);
        }
        catch (Exception ex)
        {
            _log.Warn($"[client] list_models refused ({ex.Message}) — keeping the catalogue from initialize");
            return [];
        }
    }

    /// <summary>Maps the wire rows of the slash-command list onto <see cref="SlashCommand"/>.
    /// Nameless rows are dropped — they would render as an unclickable blank in the palette.
    /// internal, unlike the model parse: `commands_changed` carries the same shape and is handled in
    /// the pane, which parses it through here rather than growing a second reader of the wire.</summary>
    internal static IReadOnlyList<SlashCommand> ParseCommands(JArray commands)
        => commands == null
            ? []
            : [.. commands.OfType<JObject>().Select(c => new SlashCommand
            {
                Name = c.Val("name", ""),
                Description = c.Val("description", ""),
                ArgumentHint = c.Val("argumentHint", ""),
                Aliases = (c["aliases"] as JArray)?.Select(x => (string)x).ToArray() ?? [],
            })
            .Where(c => !string.IsNullOrEmpty(c.Name))];

    /// <summary>Maps `effective.spinnerVerbs`; null when the CLI configures none.</summary>
    private static SpinnerVerbs ParseSpinnerVerbs(JObject verbs)
        => verbs == null
            ? null
            : new SpinnerVerbs
            {
                Mode = verbs.Val("mode"),
                Verbs = verbs["verbs"]?.ToObject<string[]>() ?? [],
            };

    /// <summary>Maps `rate_limit_event.rate_limit_info`. Only the fields we act on: the payload also
    /// carries the overage/credits block, which no surface of ours reads yet.</summary>
    internal static RateLimitInfo ParseRateLimitInfo(JObject info)
        => info == null
            ? null
            : new RateLimitInfo
            {
                Status = info.Val("status", ""),
                ResetsAt = info.Val<long?>("resetsAt", null),
                RateLimitType = info.Val("rateLimitType", ""),
                Utilization = info.Val<double?>("utilization", null),
            };

    /// <summary>Maps `result.modelUsage` (an object keyed by model id) onto its typed form. These
    /// keys are camelCase, unlike the snake_case of `message.usage` — the wire is inconsistent by
    /// design, so don't reuse one reader for both.</summary>
    internal static IReadOnlyDictionary<string, ModelUsage> ParseModelUsage(JObject modelUsage)
        => modelUsage?.Properties()
            .Where(p => p.Value is JObject)
            .ToDictionary(p => p.Name, p => new ModelUsage
            {
                InputTokens = ((JObject)p.Value).Val("inputTokens", 0),
                OutputTokens = ((JObject)p.Value).Val("outputTokens", 0),
                CacheReadInputTokens = ((JObject)p.Value).Val("cacheReadInputTokens", 0),
                CacheCreationInputTokens = ((JObject)p.Value).Val("cacheCreationInputTokens", 0),
                WebSearchRequests = ((JObject)p.Value).Val("webSearchRequests", 0),
                CostUsd = ((JObject)p.Value).Val("costUSD", 0d),
                ContextWindow = ((JObject)p.Value).Val("contextWindow", 0),
                MaxOutputTokens = ((JObject)p.Value).Val("maxOutputTokens", 0),
            });

    /// <summary>Maps the wire rows of a model catalogue onto <see cref="ModelInfo"/>. The capability
    /// flags are absent (not false) when unsupported — the CLI omits them — so every one defaults off.</summary>
    private static IReadOnlyList<ModelInfo> ParseModels(JArray models)
        => models == null
            ? []
            : [.. models.OfType<JObject>().Select(m => new ModelInfo
            {
                Value = m.Val("value", ""),
                ResolvedModel = m.Val("resolvedModel", ""),
                DisplayName = m.Val("displayName", ""),
                Description = m.Val("description", ""),
                SupportsEffort = m.ValBool("supportsEffort") ?? false,
                SupportedEffortLevels = (m["supportedEffortLevels"] as JArray)?.Select(x => (string)x).ToArray() ?? [],
                SupportsAdaptiveThinking = m.ValBool("supportsAdaptiveThinking") ?? false,
                SupportsFastMode = m.ValBool("supportsFastMode") ?? false,
                SupportsAutoMode = m.ValBool("supportsAutoMode") ?? false,
            })];

    public Task McpReconnectAsync(string serverName)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.McpReconnect, new { serverName });

    public Task McpToggleAsync(string serverName, bool enabled)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.McpToggle, new { serverName, enabled });

    public void SendPrompt(JArray contentBlocks, string uuid)
    {
        EnsureRunning();
        // CLI rejects an empty content[]; fall back to a single empty-text block.
        var content = contentBlocks?.Count > 0
                        ? contentBlocks
                        : new JArray(new JObject { ["type"] = "text", ["text"] = "" });

        var msg = new
        {
            type = "user",
            session_id = SessionId ?? "",
            message = new { role = "user", content },
            parent_tool_use_id = (string)null,
            uuid,
        };
        var transport = _transport;
        _log.Debug(() => $"[ClaudeClient.SendPrompt] BEFORE Write running={transport.IsRunning} sessionId={SessionId ?? "(none)"} blocks={contentBlocks?.Count ?? 0}");
        try
        {
            transport.Write(msg);
        }
        catch (Exception ex)
        {
            _log.LogException("ClaudeClient.SendPrompt.Write", ex);
            throw;
        }
    }

    public bool RespondToToolPermission(string toolUseId, ToolPermissionResponse response)
    {
        // Resolve+consume the request_id tracked at can_use_tool; keying by
        // tool_use_id keeps concurrent prompts from clobbering each other.
        if (string.IsNullOrEmpty(toolUseId) || !_toolRequestIds.TryRemove(toolUseId, out var requestId))
        {
            _log.Warn($"[client] permission for unknown/stale tool_use_id={toolUseId} — ignored");
            return false;
        }

        object payload;
        if (response.Allow)
        {
            // ALWAYS send updatedInput (a record, never undefined — that triggered the
            // CLI's ZodError) and updatedPermissions (the chosen permission_suggestion
            // for "allow for this session", or empty for a one-time allow).
            payload = new
            {
                behavior = "allow",
                updatedInput = response.UpdatedInput ?? [],
                updatedPermissions = response.UpdatedPermissions ?? [],
            };
        }
        else
        {
            payload = new
            {
                behavior = "deny",
                message = response.DenyMessage ?? "User denied",
            };
        }
        SendControlResponse(requestId, success: true, response: payload);
        return true;
    }

    public void RespondToHookCallback(string requestId, object response)
        => SendControlResponse(requestId, success: true, response: response);


    public void Dispose()
    {
        _transport.DisposeIntentional();
        RejectPendingRequests("ClaudeClient disposed");
    }
}

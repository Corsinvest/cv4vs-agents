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
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// Concrete <see cref="IClaudeClient"/> implementation driving claude.exe through NDJSON
/// stream-json on stdin/stdout and the bidirectional control protocol.
/// </summary>
public sealed partial class ClaudeClient : IClaudeClient
{
    private NdjsonTransport _transport = new();
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
    public string Model { get; private set; }
    public string PermissionMode { get; private set; } = Client.PermissionMode.Default;
    public AccountInfo Account { get; private set; }
    public bool IsRunning => _transport.IsRunning;

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
    public event EventHandler<AssistantTextDeltaEventArgs> AssistantTextDelta;
    public event EventHandler<AssistantThinkingDeltaEventArgs> AssistantThinkingDelta;
    public event EventHandler<ToolProgressEventArgs> ToolProgressReceived;
    public event EventHandler<JObject> SystemMessageReceived;
    public event EventHandler<string> SessionIdChanged;
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

    public ClaudeClient() => AttachTransportEvents();

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
        _transport = new NdjsonTransport();
        AttachTransportEvents();

        // Launch-only args. --include-partial-messages enables stream_event + tool_progress.
        // NOTE: no --ide here. In stream-json mode the CLI's --ide auto-connect is
        // UI-only (REPL hook) and never runs, so it does nothing. Instead we expose
        // the IDE tools as an in-process SDK MCP server: declared in the initialize
        // payload (sdkMcpServers) and registered via mcp_set_servers — the same flow
        // VS Code uses for its chat. The interactive CLI pane keeps --ide + WS lockfile.
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
        // Model is NOT passed via --model. Like VS Code, we launch without a model and
        // set it after init with a `set_model` control_request (InitializeAndPublishCatalogAsync).
        if (!string.IsNullOrEmpty(options.ResumeSessionId)) { args += " --resume " + options.ResumeSessionId; }

        // --permission-prompt-tool stdio is passed ALWAYS (except bypass): the mode decides WHICH
        // tools need confirmation, the prompt-tool is the channel to ask. Lets interactive tools
        // (AskUserQuestion, always behavior:'ask') reach the UI even in acceptEdits/plan.
        var mode = options.InitialPermissionMode ?? Client.PermissionMode.Default;
        if (mode == Client.PermissionMode.BypassPermissions)
        {
            args += " --dangerously-skip-permissions";
        }
        else
        {
            if (mode == Client.PermissionMode.AcceptEdits) { args += " --permission-mode acceptEdits"; }
            else if (mode == Client.PermissionMode.Plan) { args += " --permission-mode plan"; }
            else if (mode == Client.PermissionMode.Auto) { args += " --permission-mode auto"; }
            args += " --permission-prompt-tool stdio";
        }

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
        _transport.Start(ClaudeInstall.ResolveExecutable(), args, options.WorkingDirectory, env);

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
        // Like VS Code: the SDK MCP server names are declared in the initialize payload
        // (sdkMcpServers) AND registered via mcp_set_servers — VS Code does both.
        RegisterSdkMcpServer();
    }

    /// <summary>Register the in-process SDK MCP server so the stream-json chat can
    /// call our IDE tools (mcp__&lt;name&gt;__*). VS Code does the same via
    /// `mcp_set_servers` with a `type:"sdk"` server; the CLI then calls tools back
    /// over `mcp_message` (handled by HandleMcpMessage). No-op when unset.</summary>
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
                OutputWindowLogger.Warn($"[client] SDK MCP server '{name}' registration failed: {JsonExtensions.ToIndentedString(errors)}");
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("ClaudeClient.RegisterSdkMcpServer", ex);
        }
    }

    /// <summary>Callback id the CLI sends back on the PreToolUse hook.</summary>
    public const string AutosaveHookId = "cv_autosave";

    /// <summary>Callback ids the CLI sends back on the diagnostics hooks (pre = baseline, post = check).</summary>
    public const string DiagBaselineHookId = "cv_diag_baseline";
    public const string DiagCheckHookId = "cv_diag_check";

    private void SendInitializeHooks()
    {
        // `initialize` carries the model catalogue + rich slash commands (→ ModelsReceived) and the
        // fast-mode state; get_settings adds the model + Model-menu toggles. Together they seed the UI
        // WITHOUT a user turn — system/init only arrives on the first turn, too late to enable the
        // toolbar. Fired on every StartProcess (open + respawn).
        _ = StartupAsync();
    }

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
            if (models != null || unavailable != null || commands != null)
            {
                ModelsReceived?.Invoke(this, new ModelsReceivedEventArgs
                {
                    Models = models,
                    UnavailableModels = unavailable,
                    Commands = commands,
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
                SpinnerVerbs = eff?["spinnerVerbs"] as JObject,
                FastModeState = fastModeState,
            });
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("ClaudeClient.StartupAsync", ex);
        }
    }

    /// <summary>Ensures the process is alive, restarting with the last known options (resuming current session) if it died.</summary>
    private void EnsureRunning()
    {
        if (_transport.IsRunning) { return; }
        if (_lastOptions == null)
        {
            OutputWindowLogger.Warn("[client] SendPrompt before Prepare/StartAsync — transport not running, prompt dropped");
            return;
        }

        var replay = new ClientOptions
        {
            WorkingDirectory = WorkingDirectory ?? _lastOptions.WorkingDirectory,
            ResumeSessionId = SessionId ?? _lastOptions.ResumeSessionId,
            InitialPermissionMode = PermissionMode ?? _lastOptions.InitialPermissionMode,
            SsePort = _lastOptions.SsePort,   // keep talking to the same MCP server after a restart
            // Keep the profile's provider across respawns — else the pane silently reverts to native Claude.
            Env = _env ?? _lastOptions?.Env,
        };
        OutputWindowLogger.Info($"=== auto-restart workdir={replay.WorkingDirectory} session={replay.ResumeSessionId ?? "(none)"}");
        StartProcess(replay);
    }

    public Task StopAsync()
    {
        _transport.DisposeIntentional();
        return Task.CompletedTask;
    }

    // ----- High-level session operations (encapsulate respawn vs hot-swap) -----

    /// <summary>Starts a new empty session in the same working directory. Kills the current process and spawns a fresh one.</summary>
    public Task NewSessionAsync()
    {
        SessionId = null;
        KillForRespawn();
        return StartAsync(new ClientOptions
        {
            WorkingDirectory = WorkingDirectory,
            InitialPermissionMode = PermissionMode,
            Env = _env,
        });
    }

    /// <summary>Resumes an existing session by id. Requires respawn. The
    /// caller passes the session's own mode (read from its JSONL) so the
    /// respawned CLI runs on the SAME mode shown in the selector — not
    /// whatever the client happened to hold. Null falls back to the current.
    /// Model is not passed: the CLI's init re-emits the session's own model on --resume.</summary>
    public Task ResumeSessionAsync(string sessionId, string permissionMode = null)
    {
        KillForRespawn();
        PermissionMode = permissionMode ?? PermissionMode;
        return StartAsync(new ClientOptions
        {
            WorkingDirectory = WorkingDirectory,
            ResumeSessionId = sessionId,
            InitialPermissionMode = PermissionMode,
            Env = _env,
        });
    }

    private void KillForRespawn() => _transport.DisposeIntentional();

    // ----- Hot-swap operations -----

    public async Task SetModelAsync(string model)
    {
        await SendControlRequestAsync(ClientMessages.ControlSubtype.SetModel, new { model });
        Model = model;
    }

    public async Task SetPermissionModeAsync(string mode)
    {
        await SendControlRequestAsync(ClientMessages.ControlSubtype.SetPermissionMode, new { mode });
        PermissionMode = mode;
    }

    public Task InterruptAsync() => SendControlRequestAsync(ClientMessages.ControlSubtype.Interrupt, null);

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

    /// <summary>Enable/disable extended thinking at runtime (VS Code's channel). ON = budget 31999 +
    /// summarized display; OFF = budget 0. display is omitted when null so the CLI keeps the session mode.</summary>
    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, string display)
        => SendControlRequestAsync(
            ClientMessages.ControlSubtype.SetMaxThinkingTokens,
            display == null
                ? (object)new { max_thinking_tokens = maxThinkingTokens }
                : new { max_thinking_tokens = maxThinkingTokens, thinking_display = display });

    public async Task<JObject> GetSettingsAsync()
    {
        try { return await SendControlRequestAsync(ClientMessages.ControlSubtype.GetSettings, null); }
        catch { return null; }
    }

    public Task RewindFilesAsync(string userMessageId)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.RewindFiles, new { user_message_id = userMessageId });

    public Task StopTaskAsync(string taskId)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.StopTask, new { task_id = taskId });

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

    public Task McpReconnectAsync(string serverName)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.McpReconnect, new { serverName });

    public Task McpToggleAsync(string serverName, bool enabled)
        => SendControlRequestAsync(ClientMessages.ControlSubtype.McpToggle, new { serverName, enabled });

    // ----- Dialogue -----

    public void SendSelectionChanged(string text, string filePath, string fileUrl,
                                      int startLine, int startChar, int endLine, int endChar,
                                      bool isEmpty)
    {
        if (!_transport.IsRunning)
        {
            OutputWindowLogger.Trace("[ChatSelection] SendSelectionChanged skip: transport not running");
            return;
        }
        OutputWindowLogger.Trace(() => $"[ChatSelection] SendSelectionChanged → filePath={filePath} isEmpty={isEmpty} line={startLine}-{endLine}");
        _transport.Write(new
        {
            type = "request",
            channelId = "",
            requestId = "",
            request = new
            {
                type = "selection_changed",
                selection = new
                {
                    text = text ?? string.Empty,
                    filePath,
                    fileUrl,
                    selection = new
                    {
                        start = new { line = startLine, character = startChar },
                        end = new { line = endLine, character = endChar },
                        isEmpty,
                    },
                },
            },
        });
    }

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
        OutputWindowLogger.Debug(() => $"[ClaudeClient.SendPrompt] BEFORE Write running={_transport.IsRunning} sessionId={SessionId ?? "(none)"} blocks={contentBlocks?.Count ?? 0}");
        try
        {
            _transport.Write(msg);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("ClaudeClient.SendPrompt.Write", ex);
            throw;
        }
    }

    public bool RespondToToolPermission(string toolUseId, ToolPermissionResponse response)
    {
        // Resolve+consume the request_id tracked at can_use_tool; keying by
        // tool_use_id keeps concurrent prompts from clobbering each other.
        if (string.IsNullOrEmpty(toolUseId) || !_toolRequestIds.TryRemove(toolUseId, out var requestId))
        {
            OutputWindowLogger.Warn($"[client] permission for unknown/stale tool_use_id={toolUseId} — ignored");
            return false;
        }

        object payload;
        if (response.Allow)
        {
            // Match VS Code's PermissionResult: ALWAYS send updatedInput (a record,
            // never undefined — that triggered the CLI's ZodError) and
            // updatedPermissions (the chosen permission_suggestion for "allow for
            // this session", or empty for a one-time allow).
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// High-level typed client for claude.exe. Wraps the NDJSON control protocol
/// on stdin/stdout and exposes hot-swap operations (model, permission mode, interrupt)
/// without respawning the process.
/// </summary>
public interface IClaudeClient : IDisposable
{
    // ----- State -----
    string WorkingDirectory { get; }
    string SessionId { get; }
    string Model { get; }
    string PermissionMode { get; }
    bool IsRunning { get; }
    /// <summary>AccountInfo from the last init (email, organization,
    /// subscriptionType, apiProvider). Null until init / for 3P sessions.</summary>
    AccountInfo Account { get; }

    // ----- In-process SDK MCP server (chat IDE tools) -----
    /// <summary>Serves an inbound MCP JSON-RPC message (CLI `mcp_message`) and returns
    /// the JSON-RPC response string. Host wires this to the MCP dispatcher.</summary>
    Func<string, Task<string>> McpMessageHandler { get; set; }
    /// <summary>Name to register the in-process SDK MCP server under (tools become
    /// mcp__&lt;name&gt;__*). Null = don't register (e.g. the CLI pane uses WS instead).</summary>
    string SdkMcpServerName { get; set; }

    // ----- Lifecycle -----
    /// <summary>
    /// Configures the client without launching the process. The process will be started
    /// lazily on the first <see cref="SendPrompt"/>. If a process is already running and
    /// the working directory changed, it is killed so the next send respawns with the new options.
    /// </summary>
    void Prepare(ClientOptions options);

    /// <summary>Starts claude.exe eagerly with the given options.</summary>
    Task StartAsync(ClientOptions options);

    /// <summary>Gracefully stops the process.</summary>
    Task StopAsync();

    // ----- Session operations (encapsulate respawn vs hot-swap) -----
    Task NewSessionAsync();
    Task ResumeSessionAsync(string sessionId, string permissionMode = null);

    // ----- Hot-swap operations (no respawn) -----
    Task SetModelAsync(string model);
    Task SetPermissionModeAsync(string mode);
    Task InterruptAsync();
    Task<JObject> GetUsageAsync();
    Task<JObject> GetContextUsageAsync();
    /// <summary>Merge keys into the CLI's flag-settings layer (effortLevel,
    /// alwaysThinkingEnabled, fastMode, …). Used by the Model menu controls.</summary>
    Task ApplyFlagSettingsAsync(object settings);
    /// <summary>Enable/disable extended thinking at runtime. display parameter is omitted when null
    /// so the CLI keeps the session mode.</summary>
    Task SetMaxThinkingTokensAsync(int maxThinkingTokens, string display);
    /// <summary>Read the CLI's current settings (effortLevel, alwaysThinkingEnabled,
    /// fastMode, switchModelsOnFlag, …) to seed the Model menu toggles. Null on error.</summary>
    Task<JObject> GetSettingsAsync();
    Task RewindFilesAsync(string userMessageId);
    Task StopTaskAsync(string taskId);

    /// <summary>Generates a short AI title for the session via CLI (uses Haiku under the hood).</summary>
    Task<string> GenerateSessionTitleAsync(string description, bool persist = false);

    // ----- MCP -----
    Task<McpStatus> GetMcpStatusAsync();
    Task McpReconnectAsync(string serverName);
    Task McpToggleAsync(string serverName, bool enabled);

    // ----- Dialogue -----
    void SendSelectionChanged(string text, string filePath, string fileUrl,
                              int startLine, int startChar, int endLine, int endChar,
                              bool isEmpty);
    void SendPrompt(JArray contentBlocks, string uuid);

    /// <summary>Responds to a ToolPermissionRequested event, correlating by the
    /// stable <c>tool_use_id</c> (not the internal control request_id). Supports
    /// concurrent permission prompts — each tool maps to its own pending request.
    /// Returns false when the tool_use_id has no pending request (already answered
    /// or unknown).</summary>
    bool RespondToToolPermission(string toolUseId, ToolPermissionResponse response);

    /// <summary>Responds to a HookCallback event (raw payload, since hook response shape depends on event).</summary>
    void RespondToHookCallback(string requestId, object response);

    // ----- Events -----
    event EventHandler<InitializedEventArgs> Initialized;
    /// <summary>The CLI startup state (model + toggles from initialize+get_settings), gathered without
    /// a user turn so the UI can seed on open. Fired once per StartProcess (open + respawn).</summary>
    event EventHandler<CliStateReceivedEventArgs> CliStateReceived;
    /// <summary>Model catalogue from the `initialize` control response (models +
    /// effort levels + capability flags). How the UI learns models without a static table.</summary>
    event EventHandler<ModelsReceivedEventArgs> ModelsReceived;
    event EventHandler<AssistantMessageEventArgs> AssistantMessageReceived;
    event EventHandler<UserMessageEventArgs> UserMessageReceived;
    event EventHandler<ResultEventArgs> ResultReceived;
    event EventHandler<ToolPermissionRequestEventArgs> ToolPermissionRequested;
    /// <summary>The CLI cancelled a pending can_use_tool (interrupt / superseded turn) — the
    /// permission banner for that tool_use must be dismissed.</summary>
    event EventHandler<ToolPermissionCancelledEventArgs> ToolPermissionCancelled;
    event EventHandler<HookCallbackEventArgs> HookCallbackRequested;
    event EventHandler<RateLimitEventArgs> RateLimitReceived;
    event EventHandler<AssistantTextDeltaEventArgs> AssistantTextDelta;
    event EventHandler<AssistantThinkingDeltaEventArgs> AssistantThinkingDelta;
    event EventHandler<ToolProgressEventArgs> ToolProgressReceived;
    event EventHandler<JObject> SystemMessageReceived;
    event EventHandler<string> SessionIdChanged;
    event EventHandler<string> ConversationReset;
    event EventHandler<string> Error;
    event EventHandler<ProcessStartedEventArgs> ProcessStarted;
    event EventHandler<ProcessExitedEventArgs> ProcessExited;
}

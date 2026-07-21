/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>Wire-level type and subtype names for stream-json messages exchanged with claude.exe.</summary>
internal static class ClientMessages
{
    // ----- Top-level type field on every line of stdin/stdout -----
    public static class Type
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string System = "system";
        public const string Result = "result";
        public const string ControlRequest = "control_request";
        public const string ControlResponse = "control_response";
        // The CLI aborts one of ITS in-flight control_requests to us (typically a can_use_tool
        // whose turn was interrupted/superseded). We must drop the matching pending UI (the
        // permission banner) — else it hangs as a zombie. `request_id` at top level.
        public const string ControlCancelRequest = "control_cancel_request";
        public const string RateLimitEvent = "rate_limit_event";
        public const string StreamEvent = "stream_event";
        public const string ToolProgress = "tool_progress";
        // Emitted when the CLI resets the conversation (/clear): a new_conversation_id
        // follows, then a fresh system/init with the new session_id.
        public const string ConversationReset = "conversation_reset";
    }

    // ----- subtype on system messages -----
    public static class SystemSubtype
    {
        public const string Init = "init";
        public const string CompactBoundary = "compact_boundary";
        // Transient work status: {status:"compacting"} at start, {status:null} at end. Drives the
        // spinner's "Compacting…" label. Other status values are possible; only compacting is used.
        public const string Status = "status";
        public const string CommandsChanged = "commands_changed";
        public const string TaskStarted = "task_started";
        public const string TaskProgress = "task_progress";
        public const string TaskNotification = "task_notification";
        // Authoritative list of the session's active background tasks (agents). Empty `tasks` = none
        // running — the reliable signal for "everything, main + agents, is finished".
        public const string BackgroundTasksChanged = "background_tasks_changed";
        // Authoritative cumulative thinking-token estimate for the in-flight thinking block.
        public const string ThinkingTokens = "thinking_tokens";
    }

    // ----- subtype on control_request messages -----
    public static class ControlSubtype
    {
        // Outbound (we send these to the CLI)
        public const string Initialize = "initialize";
        public const string SetModel = "set_model";
        public const string SetPermissionMode = "set_permission_mode";
        public const string SetMaxThinkingTokens = "set_max_thinking_tokens";
        public const string Interrupt = "interrupt";
        public const string GetUsage= "get_usage";
        public const string GetContextUsage = "get_context_usage";
        public const string RewindFiles = "rewind_files";
        public const string CancelAsyncMessage = "cancel_async_message";
        public const string SeedReadState = "seed_read_state";
        public const string McpSetServers = "mcp_set_servers";
        public const string ReloadPlugins = "reload_plugins";
        public const string McpReconnect = "mcp_reconnect";
        public const string McpToggle = "mcp_toggle";
        public const string McpStatus = "mcp_status";
        public const string StopTask = "stop_task";
        public const string ApplyFlagSettings = "apply_flag_settings";
        public const string GetSettings = "get_settings";
        public const string GenerateSessionTitle = "generate_session_title";

        // Inbound (the CLI sends these to us)
        public const string CanUseTool = "can_use_tool";
        public const string HookCallback = "hook_callback";
        public const string McpMessage = "mcp_message";
        public const string Elicitation = "elicitation";

        // Response statuses
        public const string Success = "success";
        public const string Error = "error";
    }

    // ----- JSON field names on entries written to the JSONL session files -----
    public static class JsonlEntryType
    {
        public const string CustomTitle = "custom-title";
        public const string AiTitle = "ai-title";
        public const string LastPrompt = "last-prompt";
        public const string PermissionMode = "permission-mode";
    }
}

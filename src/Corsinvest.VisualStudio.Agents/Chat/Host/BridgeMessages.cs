/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// Type names for messages flowing between the WebView (JS) and the VS host (C#).
/// Organized by direction (FromWebView / ToWebView) and domain (Session, Ide, File, Cli, Open, Ui, Conv).
///
/// Naming pattern:
/// - Request  WebView -> C#:  get_<context>_<object>      (e.g. get_session_list)
/// - Command  WebView -> C#:  <verb>_<context>_<object>   (e.g. send_cli_prompt, open_ide_file)
/// - Response C# -> WebView:  <context>_<object>          (e.g. session_list, ide_active_path)
/// - Event    C# -> WebView:  <context>_<state>           (e.g. cli_started, chat_compacted)
/// </summary>
internal static class BridgeMessages
{
    public static class FromWebView
    {
        /// <summary>Session lifecycle and listing.</summary>
        public static class Session
        {
            public const string Fork = "fork_session";
        }

        /// <summary>`@` picker suggestions.</summary>
        public static class File
        {
            public const string GetSuggestions = "get_file_suggestions";
        }

        /// <summary>Control of the claude.exe CLI process.</summary>
        public static class Cli
        {
            public const string SendPrompt = "send_cli_prompt";
            public const string Stop = "stop_cli";
            public const string SetModel = "set_cli_model";
            public const string SetPermissionMode = "set_cli_permission_mode";
            public const string RespondPermission = "respond_cli_permission";
            /// <summary>Eye toggle: flips the session's SendSelection option,
            /// honoured by the MCP server when broadcasting selection_changed.</summary>
            public const string SetSendSelection = "set_cli_send_selection";
            /// <summary>Merge keys into the CLI flag-settings layer (effortLevel,
            /// alwaysThinkingEnabled, fastMode) — Model menu toggles/slider.</summary>
            public const string ApplyFlagSettings = "apply_cli_flag_settings";
            /// <summary>Thinking toggle → runtime set_max_thinking_tokens (separate from the
            /// flag-settings layer; takes effect immediately, not merged into settings).</summary>
            public const string SetMaxThinkingTokens = "cli_set_max_thinking_tokens";
        }

        /// <summary>Open something somewhere (IDE, browser, our own WebView).</summary>
        public static class Open
        {
            public const string IdeFile = "open_ide_file";
            public const string IdeFileAtEdit = "open_ide_file_at_edit";
            public const string IdeOutputWindow = "open_ide_output_window";
            public const string ToolOutput = "open_tool_output";
            public const string ExternalUrl = "open_external_url";
            public const string DiffDialog = "open_diff_dialog";
            /// <summary>Open the extension's Tools → Options page (General config).</summary>
            public const string Options = "open_options";
            /// <summary>Open a fresh interactive CLI pane (same as the toolbar "+" for CLI).</summary>
            public const string CliTerminal = "open_cli_terminal";
            /// <summary>Open this pane's session picker (same popup as the toolbar History button).</summary>
            public const string SessionHistory = "open_session_history";
            /// <summary>Open a fresh chat pane (same as the toolbar "+" for Chat).</summary>
            public const string ChatPane = "open_chat_pane";
        }

        /// <summary>Manage Claude Code plugins via `claude plugin` one-shot processes
        /// (the live chat process rejects plugin ops — only reload_plugins is in the protocol).</summary>
        public static class Plugins
        {
            public const string List = "plugins_list";
            public const string Install = "plugin_install";
            public const string Uninstall = "plugin_uninstall";
            public const string SetEnabled = "plugin_set_enabled";
            public const string MarketplaceList = "marketplace_list";
            public const string MarketplaceAdd = "marketplace_add";
            public const string MarketplaceRemove = "marketplace_remove";
            public const string MarketplaceRefresh = "marketplace_refresh";
        }

        /// <summary>Lazy fetch of stripped chat blocks (images, documents, …) and paged history.</summary>
        public static class Chat
        {
            public const string GetImage = "get_chat_image";
            public const string OpenDocument = "open_chat_document";
            /// <summary>Open an attachment still being composed. Carries the bytes inline:
            /// it isn't in the transcript yet, and the File API never gives its path.</summary>
            public const string OpenAttachment = "open_chat_attachment";
            public const string GetHistory = "get_chat_history";
            public const string GetUsage = "get_chat_usage";
            public const string GetContextUsage = "get_chat_context_usage";
            public const string GetStats = "get_chat_stats";
            /// <summary>Fire-and-forget: start the background stats indexing pass (once, on dialog
            /// open). Single-flight host-side — a no-op if one is already running.</summary>
            public const string StartStatsIndex = "start_stats_index";
            public const string GetSubagent = "get_subagent";
            public const string SubagentCancel = "subagent_cancel";
            public const string SubagentCancelAll = "subagent_cancel_all";
            public const string GetCompactSummary = "get_compact_summary";
        }

        /// <summary>WebView app-level signals to the host.</summary>
        public static class Ui
        {
            /// <summary>The app mounted and painted its first frame — hide the
            /// native "Initializing…" placeholder now.</summary>
            public const string Ready = "webview_ready";

            /// <summary>A real pointer-down happened inside the WebView: activate this pane's VS
            /// frame. Fires only on genuine clicks in the chat (never during a sibling-tab switch),
            /// unlike WPF focus events that can't cross the WebView2 HwndHost boundary.</summary>
            public const string PaneActivate = "ui_pane_activate";
        }
    }

    public static class ToWebView
    {
        /// <summary>VS IDE context pushed to the WebView (the selection badge).</summary>
        public static class Ide
        {
            public const string SelectionChanged = "ide_selection_changed";
        }

        /// <summary>`@` picker suggestion response.</summary>
        public static class File
        {
            public const string Suggestions = "file_suggestions";
        }

        /// <summary>CLI lifecycle and runtime events.</summary>
        public static class Cli
        {
            public const string Started = "cli_started";
            public const string Exited = "cli_exited";
            public const string Error = "cli_error";
            public const string ModelChanged = "cli_model_changed";
            public const string PermissionModeChanged = "cli_permission_mode_changed";
        }

        /// <summary>Chat rendering events + lazy responses.</summary>
        public static class Chat
        {
            public const string Cleared = "chat_cleared";
            public const string History = "chat_history";                 // getHistory response (scroll-up)
            public const string HistoryLoaded = "chat_history_loaded";     // unprompted push (open/resume/respawn)
            public const string PromptHistory = "chat_prompt_history";
            public const string UserText = "chat_user_text";
            public const string AssistantText = "chat_assistant_text";
            public const string AssistantTextDelta = "chat_assistant_text_delta";
            public const string ThinkingDelta = "chat_thinking_delta";
            public const string ThinkingEnded = "chat_thinking_ended";
            public const string ToolProgress = "chat_tool_progress";
            public const string ToolPermission = "chat_tool_permission";
            // CLI cancelled a pending permission (interrupt/superseded) → dismiss its banner.
            public const string ToolPermissionCancel = "chat_tool_permission_cancel";
            public const string ToolResult = "chat_tool_result";
            public const string ExchangeEnded = "chat_exchange_ended";
            public const string Compacted = "chat_compacted";
            public const string Status = "chat_status";
            public const string ImageData = "chat_image_data";
            public const string SlashCommands = "chat_slash_commands";
            public const string Usage = "chat_usage";
            public const string ContextUsage = "chat_context_usage";
            public const string Stats = "chat_stats";
            /// <summary>Notification: a background stats indexing pass finished; the WebView
            /// re-reads the stats (cache is now up to date).</summary>
            public const string StatsIndexDone = "chat_stats_index_done";
            public const string RateLimit = "chat_rate_limit";
            public const string Models = "chat_models";
            /// <summary>Response to get_subagent: full sub-agent transcript (all blocks).</summary>
            public const string SubagentLoaded = "subagent_loaded";
            /// <summary>A sub-agent (Agent/Skill/Task) started — show it as active.</summary>
            public const string SubagentStarted = "subagent_started";
            /// <summary>Sub-agent progress: last tool, summary, usage (tokens/tools/duration).</summary>
            public const string SubagentProgress = "subagent_progress";
            /// <summary>A sub-agent ended (status: completed|failed|stopped) — remove it.</summary>
            public const string SubagentEnded = "subagent_ended";
            /// <summary>Turn ended — clear all active sub-agents (safety against a missed end).</summary>
            public const string SubagentClear = "subagent_clear";
            /// <summary>Response to get_compact_summary: a compaction's full summary text.</summary>
            public const string CompactSummaryResult = "compact_summary_result";
        }

        /// <summary>UI bits (app-level state, not the WebView2 control).</summary>
        public static class Ui
        {
            public const string Init = "ui_init";
            /// <summary>Standalone push of the VS Options block (VsOptionsDto) — e.g. Tools &gt;
            /// Options changes applied live, without a full re-init.</summary>
            public const string VsSettings = "vs_settings";
            public const string ThemeChanged = "ui_theme_changed";
            /// <summary>Ask the WebView to focus the prompt box (e.g. after a session switch).</summary>
            public const string FocusInput = "ui_focus_input";
            /// <summary>Blur the prompt textarea (dual of FocusInput). Sent when the pane loses the
            /// active VS frame, so the DOM drops focus and the caret stops blinking.</summary>
            public const string BlurInput = "ui_blur_input";
            /// <summary>Pre-fill the composer with text (the forked-at message), focused and ready to send.</summary>
            public const string SetComposer = "ui_set_composer";
            /// <summary>Esc pressed: VS routed its Cancel command to the pane (ChatPaneWindow
            /// claims it so VS doesn't move focus to an open editor). The WebView decides:
            /// stop generation if busy, close an open menu, else no-op.</summary>
            public const string Escape = "ui_escape";
        }

        /// <summary>Plugin-manager results + the global "plugins changed" broadcast.</summary>
        public static class Plugins
        {
            public const string ListResult = "plugins_list_result";
            public const string MarketplaceListResult = "marketplace_list_result";
            public const string OpResult = "plugin_op_result";
            /// <summary>Broadcast to ALL live chats when an op touched active plugins → each
            /// prompt shows a non-dismissable "reload to apply" banner.</summary>
            public const string Changed = "plugins_changed";
        }
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// Handles all messages received from the WebView JS and dispatches the appropriate action.
/// </summary>
internal sealed partial class WebViewMessageHandler(WebViewBridge bridge, IClaudeClient client, PaneEntry entry)
{
    // Subscribe to the stats indexer's completion once (first GetStats), so we forward a
    // StatsIndexDone notification to this WebView when a background pass finishes.
    private bool _statsIndexHooked;

    /// <summary>This session's Claude paths, from the pane's entry (always set — created by
    /// PaneLauncher before the pane loads, so it never NREs). Evaluate on the ORIGIN thread
    /// (before/outside any Task.Run), not on a background thread.</summary>
    private ClaudePaths PaneClaudePaths => entry.ClaudePaths;

    /// <summary>Build a SessionManager keyed on this session's config-dir and working directory.
    /// New instance per call — SessionManager is stateless besides the readonly fields.</summary>
    private SessionManager Sessions => new(PaneClaudePaths, client.WorkingDirectory);

    // id is the request/response correlation id (null for notifications). The 5 request cases
    // (get_image/get_history/get_subagent/get_usage/file_get_suggestions) echo it back via
    // bridge.SendResponse(channel, id, dto); the rest ignore it.
    public void Handle(string type, JObject data, int? id)
    {
        switch (type)
        {
            case BridgeMessages.FromWebView.Cli.SendPrompt:
                HandleSendPrompt(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.RespondPermission:
                HandleRespondPermission(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.SetSendSelection:
                HandleSetSendSelection(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.ApplyFlagSettings:
                HandleApplyFlagSettings(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.SetMaxThinkingTokens:
                HandleSetMaxThinkingTokens(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.Stop:
                HandleStop(data, id);
                break;

            case BridgeMessages.FromWebView.Open.IdeFile:
                HandleIdeFile(data, id);
                break;

            case BridgeMessages.FromWebView.Open.IdeFileAtEdit:
                HandleIdeFileAtEdit(data, id);
                break;

            case BridgeMessages.FromWebView.File.GetSuggestions:
                HandleGetSuggestions(data, id);
                break;

            case BridgeMessages.FromWebView.Open.ToolOutput:
                HandleToolOutput(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetSubagent:
                HandleGetSubagent(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetImage:
                HandleGetImage(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetCompactSummary:
                HandleGetCompactSummary(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetUsage:
                HandleGetUsage(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetContextUsage:
                HandleGetContextUsage(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.StartStatsIndex:
                HandleStartStatsIndex(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetStats:
                HandleGetStats(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.SubagentCancel:
                HandleSubagentCancel(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.SubagentCancelAll:
                HandleSubagentCancelAll(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.GetHistory:
                HandleGetHistory(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.OpenAttachment:
                HandleOpenAttachment(data, id);
                break;

            case BridgeMessages.FromWebView.Chat.OpenDocument:
                HandleOpenDocument(data, id);
                break;

            case BridgeMessages.FromWebView.Open.DiffDialog:
                HandleDiffDialog(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.SetPermissionMode:
                HandleSetPermissionMode(data, id);
                break;

            case BridgeMessages.FromWebView.Cli.SetModel:
                HandleSetModel(data, id);
                break;

            case BridgeMessages.FromWebView.Session.Fork:
                HandleFork(data, id);
                break;

            case BridgeMessages.FromWebView.Open.IdeOutputWindow:
                HandleIdeOutputWindow(data, id);
                break;

            case BridgeMessages.FromWebView.Open.ExternalUrl:
                HandleExternalUrl(data, id);
                break;

            case BridgeMessages.FromWebView.Open.Options:
                HandleOptions(data, id);
                break;

            case BridgeMessages.FromWebView.Open.CliTerminal:
                HandleCliTerminal(data, id);
                break;

            case BridgeMessages.FromWebView.Open.SessionHistory:
                HandleSessionHistory(data, id);
                break;

            case BridgeMessages.FromWebView.Open.ChatPane:
                HandleChatPane(data, id);
                break;

            case BridgeMessages.FromWebView.Plugins.List:
                HandlePluginList(id);
                break;
            case BridgeMessages.FromWebView.Plugins.MarketplaceList:
                HandleMarketplaceList(id);
                break;
            case BridgeMessages.FromWebView.Plugins.Install:
                HandleInstall(data, id);
                break;
            case BridgeMessages.FromWebView.Plugins.Uninstall:
                HandleUninstall(data, id);
                break;
            case BridgeMessages.FromWebView.Plugins.SetEnabled:
                HandleSetEnabled(data, id);
                break;
            case BridgeMessages.FromWebView.Plugins.MarketplaceAdd:
                HandleMarketplaceAdd(data, id);
                break;
            case BridgeMessages.FromWebView.Plugins.MarketplaceRemove:
                HandleMarketplaceRemove(data, id);
                break;
            case BridgeMessages.FromWebView.Plugins.MarketplaceRefresh:
                HandleMarketplaceRefresh(data, id);
                break;
        }
    }

    /// <summary>Forward the stats indexer's completion to this WebView (once). When a background
    /// pass finishes the cache is up to date, so the dialog re-reads. Marshals to the UI thread.</summary>
    private void HookStatsIndexer()
    {
        if (_statsIndexHooked) { return; }
        _statsIndexHooked = true;
        Core.Stats.StatsService.IndexingCompleted += OnStatsIndexingCompleted;
    }

    // Named (not a lambda) so DisposeHandler can -= it — otherwise the static StatsService event
    // keeps this handler (and the WebView bridge) alive after the pane closes.
    private void OnStatsIndexingCompleted()
        => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.Send(BridgeMessages.ToWebView.Chat.StatsIndexDone, new { });
        }).FileAndForget(nameof(WebViewMessageHandler));

    // Called from ChatPaneControl.DisposeCore: drop the static-event subscription.
    public void Dispose()
    {
        if (_statsIndexHooked) { Core.Stats.StatsService.IndexingCompleted -= OnStatsIndexingCompleted; }
    }

}

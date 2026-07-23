/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: Chat-namespace message handlers (sub-agent/image/usage/stats/history/open-document).</summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleGetSubagent(JObject data, int? id)
    {
        if (id is not int subAgentReqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            // Read full sub-agent transcript by agentId (expand "show all").
            var subP = data.ToObject<Contracts.GetSubagentRequest>();
            var agentId = subP.AgentId ?? "";
            var sessionId = subP.SessionId ?? client.SessionId;
            if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(sessionId))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bridge.SendError(BridgeMessages.ToWebView.Chat.SubagentLoaded, subAgentReqId,
                    "sub-agent transcript unavailable (missing agentId or session)");
                return;
            }
            var page = await Task.Run(() => Sessions.ReadSubagentHistory(
                sessionId, agentId, fullFile: true));
            // Full transcript: replay every message into typed events. No parentToolUseId
            // here — the WebView routes all children under the Agent found by agentId.
            var events = HistoryReplay.ReplayPage(page.Messages, AgentsOptions.Chat.PreviewLines);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.SubagentLoaded, subAgentReqId, new Contracts.GetSubagentResponse
            {
                AgentId = agentId,
                Events = [.. events],
                HasMore = false,
            });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleGetCompactSummary(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var req = data.ToObject<Contracts.GetCompactSummaryRequest>();
            var sessionId = req.SessionId ?? client.SessionId;
            var summary = await Task.Run(() => Sessions.ReadCompactSummary(sessionId, req.Uuid));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.CompactSummaryResult, reqId, new Contracts.GetCompactSummaryResponse
            {
                Uuid = req.Uuid,
                Summary = summary,
            });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleGetImage(JObject data, int? id)
    {
        if (id is not int imageReqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = data.ToObject<Contracts.GetImageRequest>();
            var uuid = p.Uuid ?? "";
            var blockIdx = p.BlockIdx;
            var sessionId = p.SessionId ?? client.SessionId;
            if (string.IsNullOrEmpty(uuid) || blockIdx < 0)
            {
                bridge.SendError(BridgeMessages.ToWebView.Chat.ImageData, imageReqId, "invalid image request (uuid/blockIdx)");
                return;
            }
            var block = Sessions.ReadMessageBlock(sessionId, uuid, blockIdx);
            if (block?.Val("type") != "image")
            {
                bridge.SendError(BridgeMessages.ToWebView.Chat.ImageData, imageReqId, "image block not found");
                return;
            }
            var source = block["source"] as JObject;
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.ImageData, imageReqId, new Contracts.GetImageResponse
            {
                Uuid = uuid,
                BlockIdx = blockIdx,
                Base64 = source.Val("data", ""),
                MediaType = source.Val("media_type", "image/png"),
            });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleGetUsage(JObject data, int? id)
    {
        if (id is not int usageReqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            // Fetch the structured /usage data (experimental control req)
            // and pair it with the cached account info from init. The
            // webview renders both in the Account & Usage dialog.
            var usage = await client.GetUsageAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var acct = client.Account;
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.Usage, usageReqId, new Contracts.GetUsageResponse
            {
                Account = acct == null
                            ? null
                            : new Contracts.AccountDto
                            {
                                Email = acct.Email,
                                Organization = acct.Organization,
                                SubscriptionType = acct.SubscriptionType,
                                ApiProvider = acct.ApiProvider,
                            },
                Usage = usage,
            });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleGetContextUsage(JObject data, int? id)
    {
        if (id is not int ctxUsageReqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            // Snapshot of the current session's context window. The response shape is
            // stable, so map it straight into the typed DTOs (camelCase wire → PascalCase
            // via Newtonsoft's case-insensitive match; nested + gridRows[][] handled).
            var raw = await client.GetContextUsageAsync();
            var dto = raw?.ToObject<Contracts.GetContextUsageResponse>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (dto == null)
            {
                bridge.SendError(BridgeMessages.ToWebView.Chat.ContextUsage, ctxUsageReqId,
                    "context usage unavailable");
                return;
            }
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.ContextUsage, ctxUsageReqId, dto);
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleStartStatsIndex(JObject data, int? id)
    {
        // Start the background index once (dialog open). Single-flight: no-op if already
        // running. StatsIndexDone fires when it finishes → the WebView re-reads the cache.
        HookStatsIndexer();
        {
            Core.Stats.StatsService.StartIndexing(client.WorkingDirectory, PaneClaudePaths);
        }
    }

    private void HandleGetStats(JObject data, int? id)
    {
        if (id is not int statsReqId) { return; }
        {
            // Evaluate everything that reads the pane state HERE, synchronously on the Handle
            // thread — the registry entry can be dropped by the time a fire-and-forget RunAsync
            // job runs (e.g. the pane is reloading), which would NRE inside PaneClaudePaths.
            var statsPaths = PaneClaudePaths;
            var statsWd = client.WorkingDirectory;
            var statsSid = client.SessionId;
            var statsProfile = entry.Profile;
            var pStats = data.ToObject<Contracts.GetStatsRequest>();
            var statsScope = MapScope(pStats?.Scope ?? Contracts.StatsScopeDto.All);
            var statsRange = MapRange(pStats?.Range ?? Contracts.StatsRangeDto.All);
            // The WebView's "All" means "this pane's whole profile" (it has no cross-profile view);
            // Project/Session are the current workspace's project dir + open session.
            var statsSel = new Core.Stats.StatsSelection
            {
                Scope = statsScope,
                Profile = statsProfile,
                ProjectDir = statsScope == Core.Stats.StatsScope.Profile ? null : statsPaths.SessionFolder(statsWd),
                SessionIds = statsScope == Core.Stats.StatsScope.Session
                    ? new System.Collections.Generic.List<string> { statsSid }
                    : null,
            };
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                // Read-only: aggregate from the on-disk cache (fast). The background index is
                // NOT started here — that would loop (index done → re-read → GetStats → index …).
                // The WebView kicks the index once on open via StartStatsIndex.
                var dto = await Task.Run(
                    () => Core.Stats.StatsService.BuildResponse(statsSel, statsRange));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bridge.SendResponse(BridgeMessages.ToWebView.Chat.Stats, statsReqId, dto);
            }).FileAndForget(nameof(WebViewMessageHandler));
        }
    }

    private void HandleSubagentCancel(JObject data, int? id)
    {
        {
            var taskId = data.ToObject<Contracts.SubagentCancelNotification>().TaskId ?? "";
            if (!string.IsNullOrEmpty(taskId)) { _ = client?.StopTaskAsync(taskId); }
        }
    }

    private void HandleSubagentCancelAll(JObject data, int? id)
    {
        // The UI knows the active taskIds; it sends one cancel per id. As a fallback,
        // a bare cancel_all maps to interrupt (stops the whole turn).
        _ = client?.InterruptAsync();
    }

    private void HandleGetHistory(JObject data, int? id)
    {
        if (id is not int historyReqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var p = data.ToObject<Contracts.GetHistoryRequest>();
            var sessionId = p.SessionId ?? client.SessionId;
            var beforeOffset = p.BeforeOffset;
            if (string.IsNullOrEmpty(sessionId))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bridge.SendError(BridgeMessages.ToWebView.Chat.History, historyReqId, "no session for history request");
                return;
            }
            // Read + replay off the UI thread: the JSONL read (disk + parse, ~200ms on big
            // sessions) must not freeze VS. Marshal back only for the WebView post.
            var page = await Task.Run(() =>
                Sessions.ReadHistoryRaw(sessionId, SessionManager.HistoryBatchSize, beforeOffset, out _));
            var events = HistoryReplay.ReplayPage(page.Messages, AgentsOptions.Chat.PreviewLines);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.Chat.History, historyReqId, new Contracts.GetHistoryResponse
            {
                Events = [.. events],
                SessionId = sessionId,
                OldestOffset = page.OldestOffset,
                HasMore = page.HasMore,
                Prepend = true,
            });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleOpenDocument(JObject data, int? id)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = data.ToObject<Contracts.OpenDocumentNotification>();
            var uuid = p.Uuid ?? "";
            var blockIdx = p.BlockIdx;
            var sessionId = p.SessionId ?? client.SessionId;
            if (string.IsNullOrEmpty(uuid) || blockIdx < 0) { return; }
            var block = Sessions.ReadMessageBlock(sessionId, uuid, blockIdx);
            if (block?.Val("type") != "document") { return; }
            var source = block["source"] as JObject;
            var content = source.Val("data", "");
            var title = block.Val("title", "file");
            var mediaType = source.Val("media_type", "text/plain");
            // source.type (Anthropic content block): "text" carries raw UTF-8 in `data`,
            // "base64" carries base64 (PDF and other binaries).
            var sourceType = source.Val("type", "text");
            WriteTempAndOpen(title, content, mediaType, sourceType == "base64");
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleOpenAttachment(JObject data, int? id)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = data.ToObject<Contracts.OpenAttachmentNotification>();
            var name = p.Name ?? "";
            var base64 = p.Base64 ?? "";
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(base64)) { return; }
            try
            {
                // The composer reads every attachment as base64 (one code path), text files
                // included — so the bytes are never written back out as text.
                WriteTempAndOpen(name, base64, p.MediaType ?? "", isBase64: true);
            }
            catch (Exception ex)
            {
                // Nothing opens and the click looks dead otherwise — say why.
                OutputWindowLogger.LogException($"[chat] open attachment '{name}'", ex);
            }
        }).FileAndForget(nameof(WebViewMessageHandler));
    }
}

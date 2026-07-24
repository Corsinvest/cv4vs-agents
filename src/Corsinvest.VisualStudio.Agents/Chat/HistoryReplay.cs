/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Chat;

/// <summary>Replays a page of raw JSONL history messages through the SAME EmitUser/
/// EmitAssistant used live, accumulating the typed bridge events instead of sending them.
/// One construction path for live and history — no separate history parser.</summary>
internal static class HistoryReplay
{
    /// <summary>Turn a chronological JArray of history messages into typed bridge events.
    /// Each message is split by Emit* into 0..N events (text/tool_use/tool_result/user_text).
    /// A compact-boundary marker becomes a single chat_compacted event.</summary>
    public static List<Contracts.HistoryEventDto> ReplayPage(JArray messages, int previewLines)
    {
        var events = new List<Contracts.HistoryEventDto>();
        if (messages == null) { return events; }
        void Collect(string type, object data) => events.Add(new Contracts.HistoryEventDto { Type = type, Data = data });

        foreach (var m in messages)
        {
            if (m is not JObject msg) { continue; }
            var role = msg.Val("role");
            // parentToolUseId: null for main-stream messages; the sub-agent tail path
            // sets it (via _parentToolUseId) so children nest under the Agent row.
            var parentToolUseId = msg.Val("parentToolUseId");
            var uuid = msg.Val("uuid");
            var agentId = msg.Val("agentId");

            if (role == "compact")
            {
                // uuid/trigger/preTokens are lifted onto this marker by SessionManager.History
                // (TryProcessHistoryLine) from the compact_boundary line; the summary itself is
                // lazy-fetched by the WebView on demand, not carried here.
                Collect(BridgeMessages.ToWebView.Chat.Compacted, new Contracts.CompactedNotification
                {
                    Trigger = msg.Val("trigger") ?? "auto",
                    PreTokens = msg.Val("preTokens", 0),
                    Uuid = uuid ?? "",
                });
                continue;
            }
            // Pass the raw content token to whichever Emit*; each handles its own shape (EmitUser
            // also accepts a bare string for slash commands, EmitAssistant only a block array).
            var content = msg["content"];
            if (content == null) { continue; }

            var tsMs = msg.ValTimestampMs("timestamp");
            if (role == "assistant")
            {
                ContentBlockTranslator.EmitAssistant(content, Collect, parentToolUseId, msg["usage"] as JObject, timestamp: tsMs);
            }
            else // user (and tool_result-carrying user lines)
            {
                ContentBlockTranslator.EmitUser(content, previewLines, Collect, parentToolUseId, uuid, agentId, timestamp: tsMs);
            }
        }
        return events;
    }
}

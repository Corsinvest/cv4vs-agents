/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat;

/// <summary>
/// Converts assistant/user content blocks (as they arrive on the wire) into the
/// simplified messages expected by the WebView UI.
/// </summary>
internal static class ContentBlockTranslator
{
    // `needsPermission` only for synthetic can_use_tool tool_use blocks (shows the banner).
    // Normal assistant tool_use is render-only; raising the banner there would flash spuriously.
    public static void EmitAssistant(JToken content,
                                     Action<string, object> send,
                                     string parentToolUseId = null,
                                     JObject usage = null,
                                     bool needsPermission = false,
                                     JArray permissionSuggestions = null)
    {
        // JToken for a uniform Emit* signature (HistoryReplay passes the raw content to both). The
        // assistant content is always a block array in practice; a bare string never occurs, so just
        // bail on anything that isn't an array (no assistant slash-command case, unlike EmitUser).
        if (content is not JArray contentBlocks) { return; }
        // Token fields for the gauge, attached to the first event of this message so it
        // updates once per turn. Sub-agent usage (non-null parentToolUseId) is ignored TS-side.
        Contracts.ContextUsageDto usagePayload = null;
        if (usage != null)
        {
            usagePayload = new Contracts.ContextUsageDto
            {
                InputTokens = usage.Val("input_tokens", 0),
                OutputTokens = usage.Val("output_tokens", 0),
                CacheReadTokens = usage.Val("cache_read_input_tokens", 0),
                CacheCreationTokens = usage.Val("cache_creation_input_tokens", 0),
            };
        }

        var firstEmitted = false;

        foreach (var item in contentBlocks)
        {
            var type = item.Val("type");
            if (type == "text")
            {
                send(BridgeMessages.ToWebView.Chat.AssistantText, new Contracts.AssistantTextNotification
                {
                    Text = item.Val("text", ""),
                    ParentToolUseId = parentToolUseId,
                    Usage = !firstEmitted ? usagePayload : null,
                });
                firstEmitted = true;
            }
            // server_tool_use (web_search/web_fetch/code_execution…) and mcp_tool_use carry the same
            // {id,name,input} shape as tool_use, so they render as the same tool row instead of being
            // silently dropped. History-only: these are already-executed, so needsPermission stays false.
            else if (type == "tool_use" || type == "server_tool_use" || type == "mcp_tool_use")
            {
                var payload = BuildToolPermission(item, parentToolUseId, needsPermission, permissionSuggestions);
                // usage rides on the first block of the turn only (gauge counts once).
                if (!firstEmitted) { payload.Usage = usagePayload; }
                send(BridgeMessages.ToWebView.Chat.ToolPermission, payload);
                firstEmitted = true;
            }
            else if (type == "thinking" || type == "redacted_thinking")
            {
                // The final assistant message closes the thinking block: mark it done (streaming=false) so the
                // WebView flips the label to "Thought for Xs". redacted has no text (cipher-only) → static box.
                send(BridgeMessages.ToWebView.Chat.ThinkingEnded, new Contracts.ThinkingEndedNotification
                {
                    Uuid = "",                          // keyed by parentToolUseId on the WebView side
                    DurationMs = 0,                     // duration computed WebView-side from first-delta→ended
                    ParentToolUseId = parentToolUseId,
                    Redacted = type == "redacted_thinking",
                });
            }
        }
    }

    public static void EmitUser(JToken content,
                                int previewLines,
                                Action<string, object> send,
                                string parentToolUseId = null,
                                string uuid = null,
                                string agentId = null)
    {
        if (content == null) { return; }
        // agentId (the Agent tool's sub-agent id) is surfaced on the tool_result so
        // the WebView can fetch the full sub-agent transcript on expand.

        string userText = null;
        // Image/file placeholders carry only metadata + (uuid, blockIdx); TS fetches
        // lazily via get_image / open_chat_document, so no inline payloads are kept here.
        var images = new List<Contracts.UserImageDto>();
        var files = new List<Contracts.UserFileDto>();
        int blockIdx = -1;

        // A user message can carry a BARE-STRING content (not a block array): slash commands
        // (/compact, <command-name>…), command output (<local-command-stdout>…), etc. Treat it as
        // the user text — the meta filter below drops the real meta (stdout/tick/…), and the WebView
        // parses/renders slash commands itself (parseSlashCommand). Array content flows as before.
        if (content is JValue { Type: JTokenType.String } bareString)
        {
            userText = (string)bareString;
        }
        else if (content is JArray blocks)
        {
            foreach (var item in blocks)
            {
                blockIdx++;
                var type = item.Val("type");
                if (type == "tool_result")
                {
                    var resultContent = item["content"];
                    string text;
                    if (resultContent is JArray arr)
                    {
                        text = string.Join("\n", arr.Where(c => c.Val("type", "") == "text").Select(c => c.Val("text", "")));
                    }
                    else
                    {
                        text = (string)resultContent ?? "";
                    }

                    send(BridgeMessages.ToWebView.Chat.ToolResult, new Contracts.ToolResultNotification
                    {
                        ToolUseId = item.Val("tool_use_id", ""),
                        Result = TruncateLines(text, previewLines),
                        IsError = item.Val("is_error", false),
                        ParentToolUseId = parentToolUseId,
                        AgentId = agentId,
                        // Full (untruncated) non-empty line count; count-only renderers
                        // (Grep/Glob) show this instead of counting the clipped preview.
                        FullLineCount = StringHelpers.NonEmptyLineCount(text),
                    });
                }
                else if (type == "text")
                {
                    userText = item.Val("text", "");
                }
                else if (type == "image")
                {
                    var src = item["source"] as JObject;
                    images.Add(new Contracts.UserImageDto
                    {
                        Uuid = uuid,
                        BlockIdx = blockIdx,
                        MediaType = src?.Val("media_type", "image/png") ?? "image/png",
                        Preview = ThumbnailGenerator.Make(src?.Val("data", "")),
                    });
                }
                else if (type == "document")
                {
                    var title = item.Val("title", "file");
                    files.Add(new Contracts.UserFileDto
                    {
                        Name = title,
                        Uuid = uuid,
                        BlockIdx = blockIdx,
                    });
                }
            }
        }

        // Drop CLI meta-injections (task-notification, local-command output, ticks…) that ride
        // in a role:user line but aren't the user's turn — for both live and history (this is the
        // single Emit path). The WebView then never receives them and needs no filter of its own.
        // A meta line has only text (no images/files); one with attachments is a real turn.
        if (images.Count == 0 && files.Count == 0 && MetaInjection.IsMetaText(userText))
        {
            return;
        }

        if (userText != null || images.Count > 0 || files.Count > 0)
        {
            send(BridgeMessages.ToWebView.Chat.UserText, new Contracts.UserTextNotification
            {
                Text = userText ?? "",
                Images = images.Count > 0 ? [.. images] : null,
                Files = files.Count > 0 ? [.. files] : null,
                ParentToolUseId = parentToolUseId,
                Uuid = uuid,
            });
        }
    }

    private static Contracts.ToolPermissionNotification BuildToolPermission(JToken item, string parentToolUseId, bool needsPermission = false, JArray permissionSuggestions = null)
    {
        var name = item.Val("name", "");
        var id = item.Val("id", "");
        var input = item["input"] as JObject ?? [];

        // Preview: best-effort human-readable detail (command/path/most-relevant field).
        var preview = FirstNonEmpty(
            input.Val("command", ""),
            input.Val("file_path", ""),
            input.Val("path", ""),
            input.Val("pattern", ""),
            input.Val("query", ""),
            input.Val("url", ""),
            input.Val("prompt", ""),
            input.Val("skill", ""),
            input.Val("name", ""),
            input.Val("description", ""));

        return new Contracts.ToolPermissionNotification
        {
            Id = id,
            Name = name,
            Preview = preview,
            Input = input,
            ParentToolUseId = parentToolUseId,
            NeedsPermission = needsPermission,
            // Extra "allow … for this session/project" choices (CLI suggestions), passed
            // through verbatim (opaque PermissionUpdate array).
            PermissionSuggestions = [.. (permissionSuggestions ?? []).Cast<object>()],
        };
    }

    private static string TruncateLines(string text, int maxLines)
    {
        if (string.IsNullOrEmpty(text) || maxLines <= 0) { return text ?? ""; }
        var lines = text.Split('\n');
        return lines.Length <= maxLines
            ? text
            : string.Join("\n", lines, 0, maxLines) + "\n…";
    }


    /// <summary>First non-empty/non-whitespace string from the candidates, or "".</summary>
    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c)) { return c; }
        }
        return "";
    }
}

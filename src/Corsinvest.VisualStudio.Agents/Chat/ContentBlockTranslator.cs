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
/// The per-tool fields the CLI writes on a tool_result, gathered in one place instead of one
/// EmitUser parameter each — every tool that reports something of its own would otherwise widen
/// that signature, and both callers would grow another extraction.
///
/// Built from the toolUseResult where there is one (live), or from the fields history lifted onto
/// the message (replay): toolUseResult itself does not survive into the replay, so what the
/// translator needs has to travel on the message.
/// </summary>
internal sealed class ToolResultExtras
{
    /// <summary>Lines an edit landed on. (0, 0) when the tool isn't an edit or wrote a new file.</summary>
    public (int Start, int End) EditRange { get; set; }

    /// <summary>What an Agent run cost. All zero while it runs, for every other tool, and for an
    /// interrupted run — the CLI reports no totals there.</summary>
    public (long DurationMs, long Tokens, int ToolUses) AgentTotals { get; set; }

    /// <summary>Read them off a live toolUseResult. Null-safe: the object is a bare string on an
    /// error result, and both helpers already guard that shape.</summary>
    public static ToolResultExtras FromToolUseResult(JObject toolUseResult) => new()
    {
        EditRange = Core.Sessions.SessionManager.ToolUseResultEditRange(toolUseResult),
        AgentTotals = Core.Sessions.SessionManager.ToolUseResultAgentTotals(toolUseResult),
    };

    /// <summary>Read them back off a replayed message, where history lifted them as scalars. The
    /// lift stays flat on purpose: that JObject goes into the history page and is read back with
    /// Val(), so nesting there would mean serializing a sub-object inside another document.</summary>
    public static ToolResultExtras FromMessage(JObject msg) => new()
    {
        EditRange = (msg.Val("editStartLine", 0), msg.Val("editEndLine", 0)),
        AgentTotals = (msg.Val("agentDurationMs", 0L),
                       msg.Val("agentTokens", 0L),
                       msg.Val("agentToolUses", 0)),
    };

    /// <summary>The wire shape: each group present only when the tool reported it, and the whole
    /// object null when it reported neither — so the WebView tests for presence, never for a zero
    /// that could also mean "ran for 0ms".</summary>
    public Contracts.ToolResultExtrasDto ToDto()
    {
        var edit = EditRange.Start > 0
            ? new Contracts.EditLineRangeDto { StartLine = EditRange.Start, EndLine = EditRange.End }
            : null;

        var totals = AgentTotals.DurationMs > 0
            ? new Contracts.AgentRunTotalsDto
            {
                DurationMs = AgentTotals.DurationMs,
                Tokens = AgentTotals.Tokens,
                ToolUses = AgentTotals.ToolUses,
            }
            : null;

        return edit == null && totals == null
            ? null
            : new Contracts.ToolResultExtrasDto { EditRange = edit, AgentTotals = totals };
    }
}

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
                                     JArray permissionSuggestions = null,
                                     long? timestamp = null,
                                     string uuid = null,
                                     string error = null)
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
                    // Same uuid on every block of the message: it names the message, not the block.
                    Uuid = uuid,
                    Error = error,
                    Usage = !firstEmitted ? usagePayload : null,
                    Timestamp = timestamp,
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
                                string agentId = null,
                                long? timestamp = null,
                                ToolResultExtras extras = null)
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
                        var texts = arr.Where(c => c.Val("type", "") == "text").Select(c => c.Val("text", "")).ToList();
                        // The Agent tool's result is padded with blocks addressed to the model, not
                        // the user: a trailing handle ("agentId: … use SendMessage to continue this
                        // agent" + counters the UI already gets structured from task_progress), and
                        // sometimes a leading harness warning about instruction-shaped output. Both
                        // belong to the hand-off, not to the sub-agent — the transcript the history
                        // path reads has neither. Its report is the last block before the handle;
                        // keeping just that one makes live and history show the same thing.
                        text = agentId != null && texts.Count > 1
                            ? texts[texts.Count - 2]
                            : string.Join("\n", texts);
                    }
                    else
                    {
                        text = (string)resultContent ?? "";
                    }

                    send(BridgeMessages.ToWebView.Chat.ToolResult, new Contracts.ToolResultNotification
                    {
                        ToolUseId = item.Val("tool_use_id", ""),
                        // The sub-agent's report is a message to read whole, not a tool output to
                        // preview — the history path shows it in full, from the transcript. Every
                        // other tool stays capped and opens in full on demand.
                        Result = agentId == null ? TruncateLines(text, previewLines) : text,
                        IsError = item.Val("is_error", false),
                        ParentToolUseId = parentToolUseId,
                        AgentId = agentId,
                        // Full (untruncated) non-empty line count; the count-only renderers
                        // (Grep/Glob/WebSearch) show this instead of counting the clipped preview.
                        FullLineCount = StringHelpers.NonEmptyLineCount(text),
                        Extras = extras?.ToDto(),
                    });
                }
                else if (type == "text")
                {
                    // Join, don't overwrite: one submitted string can arrive as several text
                    // blocks — the IDE-context tag gets one of its own. The newline is the one
                    // the split consumed.
                    var blockText = item.Val("text", "");
                    userText = userText == null ? blockText : userText + "\n" + blockText;
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
                Timestamp = timestamp,
            });
        }
    }

    private static Contracts.ToolPermissionNotification BuildToolPermission(JToken item, string parentToolUseId, bool needsPermission, JArray permissionSuggestions)
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

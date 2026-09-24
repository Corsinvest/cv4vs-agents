/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

/// <summary>
/// SessionManager shared JSONL helpers used by the listing, history, and mutation sides:
/// the 64 KB window reader, user-text extraction, toolUseResult field access, and path-token
/// validation (the last two are also called from the WebView message handler).
/// </summary>
internal sealed partial class SessionManager
{
    /// <summary>Read up to <see cref="LiteReadWindowBytes"/> from <paramref name="offset"/>,
    /// decode UTF-8 once (single block — no chunk seams to corrupt multi-byte chars),
    /// and return the complete lines. When <paramref name="dropPartialFirstLine"/> the
    /// leading partial line (cut by the window start) is discarded.</summary>
    private static string[] ReadWindow(FileStream fs, long offset, bool dropPartialFirstLine)
    {
        var len = (int)Math.Min(LiteReadWindowBytes, fs.Length - offset);
        var buf = new byte[len];
        fs.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < len)
        {
            int n = fs.Read(buf, read, len - read);
            if (n <= 0) { break; }
            read += n;
        }
        var text = Encoding.UTF8.GetString(buf, 0, read);
        if (dropPartialFirstLine)
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0) { text = text.Substring(nl + 1); }
        }
        return text.Split('\n');
    }
    private static string ExtractUserText(string line)
    {
        // Cheap rejects before the JObject.Parse below; whitespace-tolerant so a pretty-printed
        // writer (the CLI's own output is compact) isn't silently skipped.
        if (!IsType(line, "user")) { return null; }
        if (line.IndexOf("\"tool_result\"", StringComparison.Ordinal) >= 0) { return null; }
        if (IsFlagTrue(line, "isMeta")) { return null; }
        if (IsFlagTrue(line, "isCompactSummary")) { return null; }

        try { return PromptText(JObject.Parse(line)["message"]?["content"]); }
        catch { return null; }
    }

    /// <summary><see cref="ExtractUserText"/> for a prompt sent while a turn was running, which
    /// the CLI stores as a queued_command attachment rather than a user line.</summary>
    private static string ExtractQueuedPromptText(string line)
    {
        // Cheap reject, as attachment lines are the most numerous in a transcript. Not IsType: it
        // reads the FIRST "type" in the line, and the CLI writes the nested attachment object
        // ahead of the line's own type. QueuedPrompt checks the type once parsed.
        if (line.IndexOf("\"queued_command\"", StringComparison.Ordinal) < 0) { return null; }
        try { return PromptText(QueuedPrompt(JObject.Parse(line))); }
        catch { return null; }
    }

    /// <summary>The prompt of a message the user sent while a turn was running, or null for any
    /// other line. The CLI injects it between tool calls and records it as an attachment, not as a
    /// user line. The same attachment type also carries task notifications and messages from
    /// other agents or channels: those are not the user's, and stay out.</summary>
    internal static JToken QueuedPrompt(JObject line)
    {
        if (line?.Val("type") != "attachment" || line["attachment"] is not JObject a) { return null; }
        if (a.Val("type") != "queued_command" || a.Val("commandMode", "prompt") != "prompt") { return null; }
        if (a.Val("isMeta", false) || line.Val("isSidechain", false) || a["forwardedIntent"] != null) { return null; }
        var kind = (a["origin"] as JObject)?.Val("kind");
        return kind == null || kind == "human" ? a["prompt"] : null;
    }

    /// <summary>The id the UI knows a line by. For a queued prompt that is the uuid it was sent
    /// with (source_uuid), which the live replay uses too — the line's own uuid appears nowhere
    /// else the UI could have seen it.</summary>
    private static bool IsLineFor(JObject line, string uuid)
        => line.Val("uuid", "") == uuid
        || (QueuedPrompt(line) != null && line["attachment"]?.Val("source_uuid", "") == uuid);

    /// <summary>A line's message content: message.content, or a queued prompt's.</summary>
    private static JToken LineContent(JObject line)
        => (line["message"] as JObject)?["content"] ?? QueuedPrompt(line);

    /// <summary>The typed text of a prompt's content, without the editor context the host
    /// prepends; null when nothing typed is left or it is a CLI marker.</summary>
    private static string PromptText(JToken content)
    {
        string text = null;
        if (content is JArray arr)
        {
            // NOT simply the first text block: the editor-context block cv-prompt prepends is
            // one, so stopping there yields the tag, which the strip below empties out — and
            // the prompt drops out of the ↑/↓ history. Take the first block that still holds
            // something once the tag is gone.
            foreach (var item in arr)
            {
                if (item.Val("type") != "text") { continue; }
                var candidate = Chat.MetaInjection.StripIdeContext(item.Val("text") ?? "").TrimStart();
                if (!string.IsNullOrWhiteSpace(candidate)) { text = candidate; break; }
            }
        }
        else if (content?.Type == JTokenType.String)
        {
            text = (string)content;
        }
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        // Bare-string content carries the tag inline rather than in a block of its own, so it
        // still needs stripping here; the array branch above has already done its own.
        text = Chat.MetaInjection.StripIdeContext(text).TrimStart();
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        return text.StartsWith("<") || text.StartsWith("[Request interrupted") ? null : text;
    }

    /// <summary>Read a string field from a JSONL line's top-level toolUseResult, or null.
    /// toolUseResult is often a plain string (e.g. error results), so a direct
    /// indexer access would throw — this guards the object shape.</summary>
    internal static string ToolUseResultField(JObject line, string field)
        => (line?["toolUseResult"] as JObject)?[field]?.Value<string>();

    /// <summary>What an Agent run cost, from the totals the CLI writes on its tool_result. Takes
    /// the toolUseResult object itself, like <see cref="ToolUseResultPatch"/> — live holds one,
    /// history reads it off the line.
    /// All zero when the run has no totals, which is the normal shape for an INTERRUPTED agent:
    /// there toolUseResult is a bare string ("User rejected tool use", "Error: [Request interrupted
    /// by user…]") and the CLI reports no figures at all, so the row shows none.</summary>
    internal static (long DurationMs, long Tokens, int ToolUses) ToolUseResultAgentTotals(JObject toolUseResult)
        => toolUseResult == null
            ? default
            : (toolUseResult.Val("totalDurationMs", 0L),
               toolUseResult.Val("totalTokens", 0L),
               toolUseResult.Val("totalToolUseCount", 0));

    /// <summary>The hunks the CLI computed for an edit, verbatim. Null when the result carries no
    /// patch — a Write on a new file has none — and also when it reports an empty one, which is
    /// how "nothing changed" arrives: rendering that as a diff would draw an empty box.
    /// <para>The jump the file link makes is derived from these too, WebView-side
    /// (editRangeFromHunks): one patch, one answer, so the jump and the diff cannot disagree.</para></summary>
    internal static JArray ToolUseResultPatch(JObject toolUseResult)
        => toolUseResult?["structuredPatch"] is JArray hunks && hunks.Count > 0 ? hunks : null;

    /// <summary>A path token (sessionId/agentId) is safe only if it's a plain id —
    /// letters, digits, '-' and '_'. Blocks separators and '..' traversal.</summary>
    internal static bool IsSafePathToken(string s)
    {
        if (string.IsNullOrEmpty(s)) { return false; }
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) { return false; }
        }
        return true;
    }
}

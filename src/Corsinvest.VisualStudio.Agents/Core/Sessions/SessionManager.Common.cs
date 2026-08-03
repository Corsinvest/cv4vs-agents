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

        try
        {
            var obj = JObject.Parse(line);
            var content = obj["message"]?["content"];
            string text = null;
            if (content is JArray arr)
            {
                foreach (var item in arr)
                {
                    if (item.Val("type") == "text") { text = item.Val("text"); break; }
                }
            }
            else if (content?.Type == JTokenType.String)
            {
                text = (string)content;
            }
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            // Drop the editor-context block cv-prompt prepends, so a real prompt sent with IDE
            // context is not mistaken for a "<"-tag meta line and discarded.
            text = Chat.MetaInjection.StripIdeContext(text).TrimStart();
            if (string.IsNullOrWhiteSpace(text)) { return null; }
            return text.StartsWith("<") || text.StartsWith("[Request interrupted") ? null : text;
        }
        catch { return null; }
    }
    /// <summary>Read a string field from a JSONL line's top-level toolUseResult, or null.
    /// toolUseResult is often a plain string (e.g. error results), so a direct
    /// indexer access would throw — this guards the object shape.</summary>
    internal static string ToolUseResultField(JObject line, string field)
        => (line?["toolUseResult"] as JObject)?[field]?.Value<string>();

    /// <summary>Lines the CLI computed when it applied an edit, from the first hunk of
    /// structuredPatch. Takes the toolUseResult object itself — live already holds one, history
    /// reads it off the line. (0, 0) when there is no patch (a Write on a new file has none) or
    /// the shape isn't the expected one. MultiEdit yields several non-contiguous hunks; the first
    /// is where the change starts, which is where to jump.
    /// newStart/newLines span the whole hunk, context lines included — selecting that range would
    /// highlight three untouched lines either side. The added lines are the answer, so walk
    /// `lines`: '+' and context both exist in the file after the edit and advance the counter,
    /// '-' lines are gone and don't. An all-context hunk (no '+') falls back to the whole
    /// span.</summary>
    internal static (int Start, int End) ToolUseResultEditRange(JObject toolUseResult)
    {
        if (toolUseResult?["structuredPatch"] is not JArray hunks
            || hunks.Count == 0
            || hunks[0] is not JObject first)
        {
            return (0, 0);
        }
        var hunkStart = first["newStart"]?.Value<int>() ?? 0;
        if (hunkStart <= 0) { return (0, 0); }

        var line = hunkStart;
        var addedStart = 0;
        var addedEnd = 0;
        foreach (var entry in first["lines"] as JArray ?? [])
        {
            var text = entry?.Value<string>() ?? "";
            if (text.StartsWith("-")) { continue; }
            if (text.StartsWith("+"))
            {
                if (addedStart == 0) { addedStart = line; }
                addedEnd = line;
            }
            line++;
        }
        if (addedStart > 0) { return (addedStart, addedEnd); }

        var span = first["newLines"]?.Value<int>() ?? 0;
        return span > 0 ? (hunkStart, hunkStart + span - 1) : (hunkStart, hunkStart);
    }
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

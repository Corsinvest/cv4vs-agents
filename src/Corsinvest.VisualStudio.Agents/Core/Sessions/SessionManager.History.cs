/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

/// <summary>
/// SessionManager, transcript-reading side: paged history (ReadHistoryRaw), typed-prompt
/// history (ReadUserPrompts), single-block/message lookup, and sub-agent transcript reads.
/// Session listing/metadata and mutations (rename/title/delete/fork) live in SessionManager.cs;
/// shared JSONL parsing helpers in SessionManager.Common.cs.
/// </summary>
internal sealed partial class SessionManager
{
    public HistoryPage ReadHistoryRaw(string sessionId)
        => ReadHistoryRaw(sessionId, HistoryBatchSize, -1, out _);

    public HistoryPage ReadHistoryRaw(string sessionId, int batchSize, long beforeOffset, out SessionInfo info)
    {
        using var _ = OutputWindowLogger.Global.PerfSpan($"ReadHistoryRaw({sessionId}, batch={batchSize}, before={beforeOffset})");
        var page = new HistoryPage { Messages = [] };
        var folder = FolderFor();
        var path = FileFor(sessionId);

        info = null;
        if (!File.Exists(path)) { return page; }

        var fileSize = new FileInfo(path).Length;
        OutputWindowLogger.Global.Perf(() => $"history file: {fileSize / 1024} KB");

        // `info` is built only on the initial load (beforeOffset == -1); lazy pages skip it.
        var isInitialLoad = beforeOffset < 0;
        if (isInitialLoad)
        {
            info = new SessionInfo
            {
                Id = sessionId,
                WorkingDirectory = _workingDirectory,
                LastUsedAt = File.GetLastWriteTime(path),
            };
        }

        // Reverse-read: walk the JSONL backwards in 64 KB chunks, parsing only until we've
        // collected `batchSize` messages — avoids the legacy forward scan parsing every line.
        const int ChunkBytes = 64 * 1024;

        // Newest message first while we walk backward — reversed before return.
        var messagesNewestFirst = new List<JToken>();
        // Parallel list: byte offset (start of line) for each message in `messagesNewestFirst`.
        var offsetsNewestFirst = new List<long>();
        string lastUserText = null;
        int totalLinesScanned = 0, skippedLines = 0, parsedLines = 0;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Cap the read window: when paging older content (beforeOffset > 0) exclude the
            // line at beforeOffset itself (already have it).
            long pos = isInitialLoad ? fs.Length : Math.Min(beforeOffset, fs.Length);
            // Tail holds bytes read so far; older chunks are PREPENDED so split points stay on
            // line boundaries. `tailFileOffset` = file offset of tail[0], for per-line offsets.
            byte[] tail = [];
            long tailFileOffset = pos;

            // Stop when we have the page's worth of messages AND (on the initial
            // load) the session metadata — permissionMode/model can live beyond
            // the first `batchSize` messages, so keep scanning for them even
            // after the page is full, else LoadSession falls back to "default".
            // (info is an `out` param, so the completeness check is inlined — it
            // can't be captured by a local function.)
            var localInfo = info;
            // batchSize (50) is the MINIMUM per page; then keep scanning older lines until the
            // oldest is a real user prompt, so a page always ends on the message that OPENS the
            // oldest exchange (never orphaned mid-exchange). A giant tool-only exchange makes a
            // big page, but that's the cost of not splitting an exchange.
            while (pos > 0 && (messagesNewestFirst.Count < batchSize
                || (localInfo != null && (localInfo.PermissionMode == null || localInfo.Model == null
                    || localInfo.GitBranch == null || localInfo.CliVersion == null))
                || (messagesNewestFirst.Count > 0
                    && !IsRealUserPrompt(messagesNewestFirst[messagesNewestFirst.Count - 1]))))
            {
                int chunk = (int)Math.Min(ChunkBytes, pos);
                pos -= chunk;
                var buf = new byte[chunk];
                fs.Seek(pos, SeekOrigin.Begin);
                int read = 0;
                while (read < chunk)
                {
                    int n = fs.Read(buf, read, chunk - read);
                    if (n <= 0) { break; }
                    read += n;
                }

                var combined = new byte[read + tail.Length];
                Buffer.BlockCopy(buf, 0, combined, 0, read);
                Buffer.BlockCopy(tail, 0, combined, read, tail.Length);
                long combinedFileOffset = pos;

                // Anything before the first newline is a partial line whose head is in the
                // not-yet-read older chunk; keep it as the new tail and process the rest.
                int firstNl = Array.IndexOf(combined, (byte)'\n');
                int processFrom;
                if (firstNl < 0)
                {
                    // No newline: a single line spanning >ChunkBytes. Carry the whole buffer.
                    tail = combined;
                    tailFileOffset = combinedFileOffset;
                    continue;
                }
                if (pos > 0)
                {
                    // Save the partial head for the next iteration.
                    var newTail = new byte[firstNl];
                    Buffer.BlockCopy(combined, 0, newTail, 0, firstNl);
                    tail = newTail;
                    tailFileOffset = combinedFileOffset;
                    processFrom = firstNl + 1;
                }
                else
                {
                    // Reached the start of the file: the head is a complete line too.
                    tail = [];
                    tailFileOffset = 0;
                    processFrom = 0;
                }

                // Decode the complete-lines portion to UTF-8 and split. (Tail bytes are kept raw
                // so multi-byte UTF-8 sequences split across chunks reassemble correctly.)
                var sliceLen = combined.Length - processFrom;
                var text = Encoding.UTF8.GetString(combined, processFrom, sliceLen);
                var lines = text.Split('\n');

                // Per-line byte offsets: accumulate UTF-8 byte lengths so each line's start
                // offset = combinedFileOffset + processFrom + cumulativeBytes.
                var lineOffsets = new long[lines.Length];
                long cursor = combinedFileOffset + processFrom;
                for (int i = 0; i < lines.Length; i++)
                {
                    lineOffsets[i] = cursor;
                    cursor += Encoding.UTF8.GetByteCount(lines[i]) + 1; // +1 for the '\n'
                }

                // Walk newest-to-oldest within this chunk to keep `messagesNewestFirst` order.
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var line = lines[i];
                    totalLinesScanned++;
                    if (string.IsNullOrWhiteSpace(line)) { skippedLines++; continue; }

                    // Once the page is full, keep collecting until the oldest is a real user
                    // prompt (opens the oldest exchange) — then stop adding.
                    var pageFull = messagesNewestFirst.Count >= batchSize;
                    var oldestIsUser = messagesNewestFirst.Count == 0
                        || IsRealUserPrompt(messagesNewestFirst[messagesNewestFirst.Count - 1]);
                    var sink = pageFull && oldestIsUser ? null : messagesNewestFirst;
                    int beforeCount = messagesNewestFirst.Count;
                    if (TryProcessHistoryLine(line, info, ref lastUserText, sink, ref parsedLines, ref skippedLines))
                    {
                        // Record the file offset for the message we just added.
                        if (messagesNewestFirst.Count > beforeCount)
                        {
                            offsetsNewestFirst.Add(lineOffsets[i]);
                        }
                    }
                }
            }

            // More to load if any bytes remain at file offset < oldestLineOffset.
            if (offsetsNewestFirst.Count > 0)
            {
                page.OldestOffset = offsetsNewestFirst[offsetsNewestFirst.Count - 1];
                page.HasMore = page.OldestOffset > 0;
            }
            else
            {
                // No messages loaded: caller can retry another page if not at file start.
                page.HasMore = pos > 0;
            }
        }
        catch (Exception ex) { _log.LogException("SessionManager.ReadHistoryRaw", ex); }

        if (info != null)
        {
            info.LastPrompt ??= lastUserText;
            info.Title = StringHelpers.Truncate(info.CustomTitle ?? info.AiTitle ?? info.LastPrompt, 60);
            info.MessageCount = messagesNewestFirst.Count;
        }

        // Reverse to chronological (oldest first) for the WebView.
        messagesNewestFirst.Reverse();
        // Diagnostic: how far the page walked. requestedFrom = where we started reading (file
        // length on the initial load, else beforeOffset); oldestOffset = the oldest line kept;
        // oldestIsPrompt tells whether the page ends on a real user prompt (good) or was cut by
        // reaching the file start / a metadata-only tail.
        var oldestIsPrompt = messagesNewestFirst.Count > 0 && IsRealUserPrompt(messagesNewestFirst[0]);
        OutputWindowLogger.Global.Perf(() => $"history: scanned {totalLinesScanned} lines ({skippedLines} skipped, {parsedLines} parsed), kept {messagesNewestFirst.Count} (batch={batchSize}) " +
            $"bytes[{page.OldestOffset}..{(isInitialLoad ? "END" : beforeOffset.ToString())}] hasMore={page.HasMore} oldestIsPrompt={oldestIsPrompt}");
        page.Messages = new JArray(messagesNewestFirst);
        return page;
    }

    /// <summary>Max typed prompts kept for the input ↑/↓ history.</summary>
    public const int UserPromptHistoryMax = 200;

    /// <summary>Read the user's typed prompts (for the input ↑/↓ history), returned
    /// oldest-first, walking the JSONL backward to <see cref="UserPromptHistoryMax"/>
    /// or the file start. Filters non-prompt lines via <see cref="ExtractUserText"/>;
    /// meant to run off the UI thread after the initial render.</summary>
    public List<string> ReadUserPrompts(string sessionId)
    {
        using var _ = OutputWindowLogger.Global.PerfSpan($"ReadUserPrompts({sessionId})");
        var promptsNewestFirst = new List<string>();
        var folder = FolderFor();
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return promptsNewestFirst; }

        const int ChunkBytes = 64 * 1024;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long pos = fs.Length;
            byte[] tail = [];

            while (pos > 0 && promptsNewestFirst.Count < UserPromptHistoryMax)
            {
                int chunk = (int)Math.Min(ChunkBytes, pos);
                pos -= chunk;
                var buf = new byte[chunk];
                fs.Seek(pos, SeekOrigin.Begin);
                int read = 0;
                while (read < chunk)
                {
                    int n = fs.Read(buf, read, chunk - read);
                    if (n <= 0) { break; }
                    read += n;
                }

                var combined = new byte[read + tail.Length];
                Buffer.BlockCopy(buf, 0, combined, 0, read);
                Buffer.BlockCopy(tail, 0, combined, read, tail.Length);

                int firstNl = Array.IndexOf(combined, (byte)'\n');
                int processFrom;
                if (firstNl < 0) { tail = combined; continue; }
                if (pos > 0)
                {
                    var newTail = new byte[firstNl];
                    Buffer.BlockCopy(combined, 0, newTail, 0, firstNl);
                    tail = newTail;
                    processFrom = firstNl + 1;
                }
                else { tail = []; processFrom = 0; }

                var text = Encoding.UTF8.GetString(combined, processFrom, combined.Length - processFrom);
                var lines = text.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    var pt = ExtractUserText(lines[i]);
                    if (pt != null)
                    {
                        promptsNewestFirst.Add(pt);
                        if (promptsNewestFirst.Count >= UserPromptHistoryMax) { break; }
                    }
                }
            }
        }
        catch (Exception ex) { _log.LogException("SessionManager.ReadUserPrompts", ex); }

        promptsNewestFirst.Reverse(); // chronological (oldest first) for the WebView
        return promptsNewestFirst;
    }

    /// <summary>
    /// Process a single JSONL line during the reverse history scan: parse it,
    /// route by `type`, accumulate messages and metadata. Returns true when
    /// the line contributed a message entry to <paramref name="messagesNewestFirst"/>.
    /// </summary>
    private static bool TryProcessHistoryLine(
        string line,
        SessionInfo info,
        ref string lastUserText,
        List<JToken> messagesNewestFirst,
        ref int parsedLines,
        ref int skippedLines,
        SubagentContext subagent = null)
    {
        JObject obj;
        try { obj = JObject.Parse(line); }
        catch { skippedLines++; return false; }

        parsedLines++;
        var type = obj.Val("type");
        switch (type)
        {
            case ClientMessages.Type.User:
            case ClientMessages.Type.Assistant:
                // Skip CLI-injected meta entries (e.g. <local-command-caveat>).
                if (obj.Val("isMeta", false)) { return false; }
                // The compaction summary rides as a role:user line with a plain-string
                // message.content (not the normal content array) — never add it as a
                // transcript message (matches the live path's handling); the summary
                // itself is lazy-fetched on demand, not captured here.
                if (obj.Val("isCompactSummary", false)) { return false; }
                if (obj["message"] is not JObject msg) { return false; }
                // NOTE: keep user messages that carry only tool_result blocks — the WebView
                // walks them to populate each assistant tool_use's OUT cell. cv-message guards
                // against rendering an empty user bubble for them.
                if (messagesNewestFirst != null)
                {
                    // Heavy blocks (image/document strip, tool_result truncate) are now
                    // handled by EmitUser during HistoryReplay — the messages travel raw.
                    // Lift the line's top-level uuid onto the message (internal replay tag,
                    // read by HistoryReplay; the message object has no native uuid).
                    var uuid = obj.Val("uuid");
                    if (!string.IsNullOrEmpty(uuid)) { msg["uuid"] = uuid; }
                    // The message time is a top-level field on the record, not inside `message`;
                    // lift it too so HistoryReplay's ValTimestampMs finds it (the "x ago" on replay).
                    var ts = obj.Val("timestamp");
                    if (!string.IsNullOrEmpty(ts)) { msg["timestamp"] = ts; }
                    // The sub-agent this line SPAWNED, so the WebView knows which transcript to
                    // open when the row is expanded. The main JSONL carries it inline on
                    // toolUseResult.agentId (camelCase on disk); a transcript has no
                    // toolUseResult at all, so there the link survives only in the sidecars.
                    // Not to be confused with the line's own top-level agentId, which names the
                    // file the line sits in — a different question, answered WebView-side.
                    var agentId = subagent != null
                        ? subagent.SpawnedAgentIdFor(msg)
                        : ToolUseResultField(obj, "agentId");
                    if (!string.IsNullOrEmpty(agentId)) { msg["agentId"] = agentId; }
                    // Same lift for the edit's line range: toolUseResult doesn't survive into the
                    // replay, so what HistoryReplay needs has to travel on the message itself.
                    var editRange = ToolUseResultEditRange(obj["toolUseResult"] as JObject);
                    if (editRange.Start > 0)
                    {
                        msg["editStartLine"] = editRange.Start;
                        msg["editEndLine"] = editRange.End;
                    }
                    // Same lift again, for what an Agent run cost. Only a completed run has these:
                    // an interrupted one leaves toolUseResult a bare string, so nothing is lifted
                    // and the row shows no figures — the same as live.
                    var agentTotals = ToolUseResultAgentTotals(obj["toolUseResult"] as JObject);
                    if (agentTotals.DurationMs > 0)
                    {
                        msg["agentDurationMs"] = agentTotals.DurationMs;
                        msg["agentTokens"] = agentTotals.Tokens;
                        msg["agentToolUses"] = agentTotals.ToolUses;
                    }
                    messagesNewestFirst.Add(msg);
                }

                // We walk newest→oldest, so the first entry seen is freshest: only set if unset.
                // `info` is null on lazy-load pages — skip metadata accumulation then.
                if (info != null)
                {
                    if (info.GitBranch == null)
                    {
                        var gb = obj.Val("gitBranch");
                        if (!string.IsNullOrEmpty(gb)) { info.GitBranch = gb; }
                    }
                    if (info.CliVersion == null)
                    {
                        var ver = obj.Val("version");
                        if (!string.IsNullOrEmpty(ver)) { info.CliVersion = ver; }
                    }
                    if (type == ClientMessages.Type.User)
                    {
                        if (info.PermissionMode == null)
                        {
                            var pm = obj.Val("permissionMode");
                            if (!string.IsNullOrEmpty(pm)) { info.PermissionMode = pm; }
                        }
                        if (lastUserText == null)
                        {
                            var t = ExtractUserText(line);
                            if (t != null) { lastUserText = t; }
                        }
                    }
                    else if (type == ClientMessages.Type.Assistant)
                    {
                        // Model is at `message.model` on assistant lines only; skip the CLI's
                        // synthetic placeholder used for tool-result echoes.
                        if (info.Model == null)
                        {
                            var mdl = msg.Val("model");
                            if (!string.IsNullOrEmpty(mdl) && mdl != "<synthetic>") { info.Model = mdl; }
                        }
                    }
                }
                return true;

            case ClientMessages.JsonlEntryType.CustomTitle:
                if (info != null && info.CustomTitle == null) { info.CustomTitle = obj.Val("customTitle"); }
                return false;
            case ClientMessages.JsonlEntryType.AiTitle:
                if (info != null && info.AiTitle == null) { info.AiTitle = obj.Val("aiTitle"); }
                return false;
            case ClientMessages.JsonlEntryType.LastPrompt:
                if (info != null && info.LastPrompt == null) { info.LastPrompt = obj.Val("lastPrompt"); }
                return false;

            case ClientMessages.Type.System:
                var systemSubtype = obj.Val("subtype", "");
                if (systemSubtype == ClientMessages.SystemSubtype.CompactBoundary)
                {
                    // sink is null once the page is full and we're only scanning older lines
                    // for the opening user prompt/metadata — don't add a compact row then.
                    if (messagesNewestFirst != null)
                    {
                        var meta = obj["compactMetadata"] as JObject;
                        messagesNewestFirst.Add(new JObject
                        {
                            ["role"] = "compact",
                            ["uuid"] = obj.Val("uuid", ""),
                            ["trigger"] = meta?.Val("trigger") ?? "auto",
                            ["preTokens"] = meta?.Val("preTokens", 0) ?? 0,
                        });
                    }
                    return true;
                }
                if (systemSubtype == ClientMessages.SystemSubtype.LocalCommand)
                {
                    // A slash command's local output persisted as a system record. Replay it as a
                    // user-text line: EmitUser accepts the bare "<local-command-stdout>…" string and
                    // the WebView turns it into a slash-result pill (same as the live path).
                    if (messagesNewestFirst != null)
                    {
                        var content = obj.Val("content", "");
                        if (content.IndexOf("<local-command-std", System.StringComparison.Ordinal) >= 0)
                        {
                            messagesNewestFirst.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = content,
                                ["uuid"] = obj.Val("uuid", ""),
                                // Carry the ISO timestamp so ReplayPage → the actions row shows "x ago".
                                ["timestamp"] = obj.Val("timestamp", (string)null),
                            });
                        }
                    }
                    return true;
                }
                return false;
        }

        return false;
    }

    public JObject ReadMessageBlock(string sessionId, string uuid, int blockIdx)
    {
        if (string.IsNullOrEmpty(uuid)) { return null; }
        var folder = FolderFor();
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return null; }
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(uuid)) { continue; }
                JObject obj;
                try { obj = JObject.Parse(line); } catch { continue; }
                if (obj.Val("uuid", "") != uuid) { continue; }
                return obj["message"]?["content"] is not JArray content || blockIdx < 0 || blockIdx >= content.Count
                    ? null
                    : content[blockIdx] as JObject;
            }
        }
        catch (Exception ex) { _log.LogException("SessionManager.ReadMessageBlock", ex); }
        return null;
    }

    /// <summary>Read the compaction summary that follows the compact_boundary with the given uuid:
    /// the next .jsonl line, flagged isCompactSummary with a string content. "" if absent.</summary>
    public string ReadCompactSummary(string sessionId, string boundaryUuid)
    {
        if (!IsSafePathToken(sessionId) || string.IsNullOrEmpty(boundaryUuid)) { return ""; }
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return ""; }
        try
        {
            var found = false;
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                if (!found)
                {
                    if (!line.Contains(boundaryUuid)) { continue; }
                    JObject b; try { b = JObject.Parse(line); } catch { continue; }
                    if (b.Val("uuid", "") != boundaryUuid) { continue; }
                    found = true; // the summary is the NEXT non-empty line
                    continue;
                }
                JObject s; try { s = JObject.Parse(line); } catch { return ""; }
                if (s.Val("isCompactSummary", false) && s["message"]?["content"] is { Type: JTokenType.String } c)
                {
                    return (string)c;
                }
                return ""; // next line wasn't the summary
            }
        }
        catch (Exception ex) { _log.LogException("SessionManager.ReadCompactSummary", ex); }
        return "";
    }

    /// <summary>Build a HistoryPage from a flat array of JSONL lines (chronological order).
    /// Used by ReadSubagentHistory (forward pass). ReadHistoryRaw keeps its own reverse-scan
    /// paginated reader — its algorithm can't share this simple forward builder.</summary>
    private static HistoryPage BuildHistoryPageFromLines(string[] lines, SubagentContext subagent = null)
    {
        var messages = new List<JToken>();
        string lastUserText = null;
        int parsed = 0, skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            TryProcessHistoryLine(line, null, ref lastUserText, messages, ref parsed, ref skipped, subagent);
        }
        return new HistoryPage { Messages = new JArray(messages) };
    }

    /// <summary>Which Agent row spawned which sub-agent, keyed by tool_use_id. A transcript can't
    /// say it: toolUseResult — where the main JSONL carries the id inline — never appears in one,
    /// so the link survives only in the subagents/*.meta.json sidecars the CLI writes beside it.
    /// ~150 bytes each, read once per page.</summary>
    private sealed class SubagentContext
    {
        private readonly Dictionary<string, string> _spawnedByToolUse =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A session with no sidecars yields an empty map, which leaves every row exactly
        /// as it was before this existed.</summary>
        public static SubagentContext Read(string dir)
        {
            var ctx = new SubagentContext();
            if (!Directory.Exists(dir)) { return ctx; }
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "agent-*.meta.json"))
                {
                    try
                    {
                        var toolUseId = JObject.Parse(File.ReadAllText(file, Encoding.UTF8)).Val("toolUseId");
                        if (string.IsNullOrEmpty(toolUseId)) { continue; }
                        // The agentId IS the file name (agent-<id>.meta.json) — the same convention
                        // the transcript uses, so it can be opened straight away.
                        var name = Path.GetFileNameWithoutExtension(file);   // agent-<id>.meta
                        ctx._spawnedByToolUse[toolUseId] =
                            name.Substring("agent-".Length, name.Length - "agent-".Length - ".meta".Length);
                    }
                    catch { /* a malformed sidecar only costs that one link */ }
                }
            }
            // Static nested helper: no SessionManager instance, so no pane tag here.
            catch (Exception ex) { OutputWindowLogger.Global.LogException("SessionManager.SubagentContext.Read", ex); }
            return ctx;
        }

        /// <summary>The sub-agent a message spawned, or null when it isn't an Agent tool_result.
        /// Keyed by the tool_use_id it answers.</summary>
        public string SpawnedAgentIdFor(JToken msg)
        {
            if (_spawnedByToolUse.Count == 0 || msg?["content"] is not JArray blocks) { return null; }
            foreach (var b in blocks)
            {
                if (b.Val("type", "") != "tool_result") { continue; }
                return _spawnedByToolUse.TryGetValue(b.Val("tool_use_id", ""), out var spawned) ? spawned : null;
            }
            return null;
        }
    }

    /// <summary>Read a sub-agent transcript (subagents/agent-<agentId>.jsonl) into a HistoryPage,
    /// same shape as ReadHistoryRaw. fullFile=false reads only the tail (enough for the last few
    /// tools); true reads the whole file. Missing file or empty agentId → empty page (no throw).</summary>
    public HistoryPage ReadSubagentHistory(string sessionId, string agentId, bool fullFile)
    {
        using var _ = OutputWindowLogger.Global.PerfSpan($"ReadSubagentHistory({agentId}, full={fullFile})");
        // agentId/sessionId reach here from the WebView; reject anything that isn't a
        // plain id token so they can't traverse out of the session dir into the path.
        if (!IsSafePathToken(sessionId) || !IsSafePathToken(agentId)) { return new HistoryPage { Messages = [] }; }
        var folder = FolderFor();
        var dir = LongPath(Path.Combine(folder, sessionId, "subagents"));
        var path = Path.Combine(dir, $"agent-{agentId}.jsonl");
        if (!File.Exists(path)) { return new HistoryPage { Messages = [] }; }
        try
        {
            // fullFile reads the whole transcript (expand "show all") — ReadWindow caps
            // at LiteReadWindowBytes, which would silently drop the newest entries of a
            // file larger than 64 KB. The tail path keeps the cheap 64 KB window.
            string[] lines;
            if (fullFile)
            {
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            else
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var size = fs.Length;
                lines = ReadWindow(fs, Math.Max(0, size - LiteReadWindowBytes), dropPartialFirstLine: size > LiteReadWindowBytes);
            }
            return BuildHistoryPageFromLines(lines, SubagentContext.Read(dir));
        }
        catch (Exception ex) { _log.LogException("SessionManager.ReadSubagentHistory", ex); return new HistoryPage { Messages = [] }; }
    }
}

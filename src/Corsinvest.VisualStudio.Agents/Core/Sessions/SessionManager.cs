/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

/// <summary>Reads/writes CLI session JSONL files for one config-dir. The config-dir is
/// constant for a pane's lifetime, so it's injected once via the constructor rather than
/// threaded through every call — a config-dir can never be silently forgotten.</summary>
public sealed partial class SessionManager
{
    // VS Code-style metadata scan: read a fixed 64 KB window from the start and
    // from the end of the JSONL in one read each, decode once, scan the lines.
    // Title rows (custom-title / ai-title / last-prompt) and the freshest
    // git/version/model/permissionMode all live within these windows. Reading in
    // a single block avoids the multi-byte-UTF-8 corruption a chunked backward
    // scan hit at chunk boundaries (garbled accented chars in titles).
    private const int LiteReadWindowBytes = 64 * 1024;

    private readonly ClaudePaths _paths;
    private readonly string _workingDirectory;
    public SessionManager(ClaudePaths paths, string workingDirectory)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    // The session folder / a session .jsonl path for this instance's workdir, honouring
    // this instance's config-dir. One place each so the path construction lives in a single spot.
    private string FolderFor() => _paths.SessionFolder(_workingDirectory);
    private string FileFor(string sessionId) => Path.Combine(_paths.SessionFolder(_workingDirectory), sessionId + ".jsonl");

    public List<SessionInfo> Load()
    {
        using var _ = OutputWindowLogger.PerfSpan("SessionManager.Load");
        var folder = FolderFor();
        if (!Directory.Exists(folder)) { return []; }

        var files = new DirectoryInfo(folder).GetFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTime)
            .ToArray();

        OutputWindowLogger.Perf(() => $"sessions: loading {files.Length} files");

        return files
            .AsParallel()
            .Select(f => ReadSessionFile(f.FullName))
            .Where(x => x != null)
            .OrderByDescending(x => x.LastUsedAt)
            .ToList();
    }

    // Instance method (not static): needs _workingDirectory to stamp SessionInfo.WorkingDirectory.
    // Called only from Load(), including from its .AsParallel() — reading a readonly field
    // concurrently is safe, so this doesn't need to stay static.
    private SessionInfo ReadSessionFile(string path)
    {
        try
        {
            var info = ScanMetadata(path);
            if (info == null) { return null; }

            info.Id = Path.GetFileNameWithoutExtension(path);
            info.WorkingDirectory = _workingDirectory;
            info.LastUsedAt = File.GetLastWriteTime(path);

            var rawTitle = info.CustomTitle ?? info.AiTitle ?? info.LastPrompt;
            if (rawTitle == null) { return null; }
            info.Title = StringHelpers.Truncate(rawTitle, 60);
            return info;
        }
        catch { OutputWindowLogger.Debug(() => "[sessions] skipped an unreadable/corrupt session file"); return null; }
    }

    private static SessionInfo ScanMetadata(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long size = fs.Length;
        if (size == 0) { return null; }

        // Tail first (freshest title + metadata), then head as fallback for fields
        // a long session pushed out of the tail window (e.g. the original mode).
        var tail = ReadWindow(fs, Math.Max(0, size - LiteReadWindowBytes), dropPartialFirstLine: size > LiteReadWindowBytes);
        var head = size > LiteReadWindowBytes
            ? ReadWindow(fs, 0, dropPartialFirstLine: false)
            : null; // small file: tail already covers everything

        var info = new SessionInfo();
        ScanLines(tail, info, newestFirst: true);
        if (head != null) { ScanLines(head, info, newestFirst: false); }

        return info.IsSidechain ? null : info;
    }

    /// <summary>Pull title/metadata fields out of the given lines into <paramref name="info"/>.
    /// Only fills fields still unset, so a tail-then-head order keeps the freshest values.
    /// <paramref name="newestFirst"/> walks bottom-up (tail) so the most recent rows win.</summary>
    private static void ScanLines(string[] lines, SessionInfo info, bool newestFirst)
    {
        int start = newestFirst ? lines.Length - 1 : 0;
        int step = newestFirst ? -1 : 1;
        for (int i = start; i >= 0 && i < lines.Length; i += step)
        {
            var line = lines[i];
            if (line.Length == 0) { continue; }

            info.CustomTitle ??= MatchField(line, "custom-title", "customTitle");
            info.AiTitle ??= MatchField(line, "ai-title", "aiTitle");
            info.LastPrompt ??= MatchField(line, "last-prompt", "lastPrompt")
                               ?? ExtractUserText(line);
            bool isUser = IsType(line, "user");
            bool isAssistant = IsType(line, "assistant");
            bool isTurnLine = isUser || isAssistant;
            if (isTurnLine)
            {
                info.GitBranch ??= FindJsonStringValue(line, "gitBranch");
                info.CliVersion ??= FindJsonStringValue(line, "version");
                if (info.PermissionMode == null && isUser)
                {
                    info.PermissionMode = FindJsonStringValue(line, "permissionMode");
                }
                // Model lives on assistant lines (message.model); take the most recent
                // non-synthetic one so a fresh pane inherits the last-used model.
                if (info.Model == null && isAssistant)
                {
                    var mdl = FindJsonStringValue(line, "model");
                    if (!string.IsNullOrEmpty(mdl) && mdl != "<synthetic>") { info.Model = mdl; }
                }
            }

            if (IsFlagTrue(line, "isSidechain")) { info.IsSidechain = true; }
        }
    }

    /// <summary>True when the line's top-level "type" equals <paramref name="type"/>. Goes through
    /// FindJsonStringValue rather than matching a literal '"type":"x"' so a writer that pretty-prints
    /// (space after the colon) is read the same as the CLI's compact output.</summary>
    private static bool IsType(string line, string type)
        => string.Equals(FindJsonStringValue(line, "type"), type, StringComparison.Ordinal);

    private static string MatchField(string line, string type, string field)
        => IsType(line, type) ? FindJsonStringValue(line, field) : null;

    /// <summary>True when the line carries <c>"key": true</c>. Tolerates the space after the colon
    /// for the same reason as <see cref="IsType"/>.</summary>
    internal static bool IsFlagTrue(string line, string key)
        => line.IndexOf("\"" + key + "\":true", StringComparison.Ordinal) >= 0
        || line.IndexOf("\"" + key + "\": true", StringComparison.Ordinal) >= 0;

    // Extracts text from a user message line — used as LastPrompt fallback when no last-prompt entry exists.
    /// <summary>True only for a genuine user PROMPT (opens an exchange), not a synthetic
    /// role:user line. The CLI writes tool_results as role:user with a content ARRAY, and
    /// meta-injections (task-notification/system-reminder/local-command…) as role:user with
    /// text starting '&lt;'. History paging scrolls past those to the real prompt so the oldest
    /// exchange isn't orphaned — but bounded (see the page loop) so a long tool-only stretch
    /// doesn't load the whole session in one page.</summary>
    // CLI-injected entries that ride in a role:user line but aren't the user's own turn.
    /// <summary>True only for a genuine user PROMPT that OPENS an exchange — not a tool_result
    /// (content is an array) nor a CLI meta-injection (Chat.MetaInjection, the same filter used
    /// to drop them from the transcript). History paging stops on one of these so the oldest
    /// exchange isn't orphaned.</summary>
    private static bool IsRealUserPrompt(JToken msg)
    {
        if (msg?["role"]?.Value<string>() != "user") { return false; }
        var content = msg["content"];

        // A real user turn: typed text that isn't a CLI meta-injection. An interrupt marker
        // ([Request interrupted…]) counts — it's a real turn (rendered with an orange bar),
        // so the history scroll may anchor on it like any prompt.
        static bool IsRealText(string t)
            => !string.IsNullOrWhiteSpace(t) && !Chat.MetaInjection.IsMetaText(t);

        // Old shape: content is a bare string.
        if (content is { Type: JTokenType.String })
        {
            return IsRealText((string)content);
        }

        // Current shape: content is a block array. A real prompt carries a text block
        // (optionally alongside image/document blocks). A tool_result-only turn is NOT a prompt.
        if (content is JArray blocks)
        {
            var textBlock = blocks.FirstOrDefault(b => b?["type"]?.Value<string>() == "text");
            return textBlock != null && IsRealText(textBlock.Val("text", ""));
        }

        return false;
    }

    private static string FindJsonStringValue(string text, string key)
    {
        var p1 = "\"" + key + "\":\"";
        var p2 = "\"" + key + "\": \"";
        var idx = text.IndexOf(p1, StringComparison.Ordinal);
        var patternLen = p1.Length;
        if (idx < 0) { idx = text.IndexOf(p2, StringComparison.Ordinal); patternLen = p2.Length; }
        if (idx < 0) { return null; }

        int valueStart = idx + patternLen;
        int i = valueStart;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '\\') { i += 2; continue; }
            if (ch == '"') { return UnescapeJsonString(text.Substring(valueStart, i - valueStart)); }
            i++;
        }
        return null;
    }

    private static string UnescapeJsonString(string raw)
    {
        if (raw.IndexOf('\\') < 0) { return raw; }
        try { return JsonConvert.DeserializeObject<string>("\"" + raw + "\""); }
        catch { return raw; }
    }

    /// <summary>
    /// One page of chat history loaded by <see cref="ReadHistoryRaw"/>.
    /// </summary>
    public sealed class HistoryPage
    {
        /// <summary>Messages in chronological order (oldest first).</summary>
        public JArray Messages { get; set; }
        /// <summary>
        /// Byte offset (in the JSONL file) of the line that holds the OLDEST
        /// message returned in this page. Pass it back as <c>beforeOffset</c>
        /// to the next call to load older messages. -1 when the page is empty.
        /// </summary>
        public long OldestOffset { get; set; } = -1;
        /// <summary>True when there are messages older than <see cref="OldestOffset"/>.</summary>
        public bool HasMore { get; set; }
    }

    /// <summary>Batch size for initial load and lazy pages: fills the viewport plus a scroll
    /// buffer before triggering the next lazy fetch.</summary>
    public const int HistoryBatchSize = 50;


    public string GetLatestSessionId()
    {
        var folder = FolderFor();
        return !Directory.Exists(folder)
            ? null
            : Directory.GetFiles(folder, "*.jsonl")
                        .OrderByDescending(f => File.GetLastWriteTime(f))
                        .Select(f => Path.GetFileNameWithoutExtension(f))
                        .FirstOrDefault();
    }

    public void Rename(string sessionId, string newTitle)
    {
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return; }

        var entry = new JObject
        {
            ["type"] = ClientMessages.JsonlEntryType.CustomTitle,
            ["customTitle"] = newTitle,
            ["sessionId"] = sessionId
        };
        File.AppendAllText(path, "\n" + JsonConvert.SerializeObject(entry));
    }

    /// <summary>True if the session already has a title (user rename or a prior AI
    /// title). Lets callers skip the CLI round-trip when there's nothing to generate.</summary>
    public bool HasTitle(string sessionId)
    {
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return false; }
        var existing = ScanMetadata(path);
        return existing?.CustomTitle != null || existing?.AiTitle != null;
    }

    /// <summary>Resolve the display title for one session (same priority and head+tail
    /// scan the session list uses): custom > ai > last-prompt. Null when none / missing.
    /// Used by the pane toolbar to show + refresh the current session's title.</summary>
    public string ScanTitle(string sessionId)
    {
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return null; }
        var info = ScanMetadata(path);
        return info?.CustomTitle ?? info?.AiTitle ?? info?.LastPrompt;
    }

    // Writes the AI title unless a title already exists (custom = user rename, or a
    // prior ai title). Returns true when it was a no-op (title already present).
    public bool SetAiTitle(string sessionId, string aiTitle)
    {
        var path = FileFor(sessionId);
        if (!File.Exists(path)) { return false; }

        var existing = ScanMetadata(path);
        if (existing?.CustomTitle != null || existing?.AiTitle != null) { return true; }

        var entry = new JObject
        {
            ["type"] = ClientMessages.JsonlEntryType.AiTitle,
            ["aiTitle"] = aiTitle,
            ["sessionId"] = sessionId
        };
        File.AppendAllText(path, "\n" + JsonConvert.SerializeObject(entry));
        return false;
    }

    public void Delete(string sessionId)
    {
        var path = FileFor(sessionId);
        if (File.Exists(path)) { File.Delete(path); }
    }

    /// <summary>Result of a fork: the new session id and the text of the cut
    /// message, which the caller pre-fills into the new pane's composer.</summary>
    public sealed class ForkResult
    {
        public string NewSessionId { get; set; }
        /// <summary>Text of the forked-at message — excluded from the transcript
        /// and handed back so the user can edit/resend it (VS Code behaviour).</summary>
        public string ExcludedPrompt { get; set; }
    }

    /// <summary>Forks a session into a brand-new JSONL: copies messages up to but
    /// EXCLUDING <paramref name="resumeAtMessageUuid"/> (VS Code forks before the
    /// clicked message and hands its text back for editing) and rewrites every uuid
    /// (plus parentUuid / leafUuid / messageId) to fresh ids, so the new transcript
    /// shares no message identity with the source. Stamps the new sessionId on each
    /// line. Returns null if the source is missing or the cut point isn't found.</summary>
    public ForkResult ForkSession(string sessionId, string resumeAtMessageUuid)
    {
        var folder = FolderFor();
        var srcPath = Path.Combine(folder, sessionId + ".jsonl");
        if (!File.Exists(srcPath)) { return null; }

        var newSessionId = Guid.NewGuid().ToString();
        var dstPath = Path.Combine(folder, newSessionId + ".jsonl");
        string excludedPrompt = null;

        try
        {
            var kept = new List<JObject>();
            var uuidMap = new Dictionary<string, string>();
            var foundCut = false;
            foreach (var line in File.ReadLines(srcPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                JObject obj;
                try { obj = JObject.Parse(line); }
                catch { continue; }

                if (obj.Val("type") == "progress") { continue; } // transient, not part of the transcript

                var uuid = obj.Val("uuid");
                // The cut message is excluded: grab its text for the composer and stop.
                if (!string.IsNullOrEmpty(uuid) && uuid == resumeAtMessageUuid)
                {
                    excludedPrompt = ExtractMessageText(obj);
                    foundCut = true;
                    break;
                }
                if (!string.IsNullOrEmpty(uuid)) { uuidMap[uuid] = Guid.NewGuid().ToString(); }
                kept.Add(obj);
            }
            if (!foundCut) { OutputWindowLogger.Debug(() => "[sessions] fork-at uuid not found in source session → fork aborted"); return null; }

            // Remap every id that references a message — leaving one pointing at an
            // old uuid would dangle, since those ids are all regenerated above.
            using var writer = new StreamWriter(dstPath, append: false, Encoding.UTF8);
            foreach (var obj in kept)
            {
                RemapUuid(obj, "uuid", uuidMap);
                RemapUuid(obj, "parentUuid", uuidMap);
                RemapUuid(obj, "leafUuid", uuidMap);
                RemapUuid(obj, "messageId", uuidMap);
                if (obj["snapshot"] is JObject snap) { RemapUuid(snap, "messageId", uuidMap); }
                if (obj["sessionId"] != null) { obj["sessionId"] = newSessionId; }
                writer.WriteLine(obj.ToString(Formatting.None));
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("SessionManager.fork", ex); return null; }

        return new ForkResult { NewSessionId = newSessionId, ExcludedPrompt = excludedPrompt };
    }

    /// <summary>Plain text of a user/assistant JSONL entry: joins the text blocks
    /// of message.content (string or block array). Empty when there's no text.</summary>
    private static string ExtractMessageText(JObject obj)
    {
        if (obj["message"] is not JObject msg) { return ""; }
        var content = msg["content"];
        if (content is JValue v) { return v.Value<string>() ?? ""; }
        if (content is not JArray blocks) { return ""; }
        var parts = blocks
            .Where(b => b.Val("type") == "text")
            .Select(b => b.Val("text", ""))
            .Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", parts);
    }

    /// <summary>Replace <paramref name="prop"/> on <paramref name="obj"/> with its
    /// remapped uuid, if present in the map. No-op when the field is absent or unmapped.</summary>
    private static void RemapUuid(JObject obj, string prop, Dictionary<string, string> map)
    {
        var val = obj.Val(prop);
        if (!string.IsNullOrEmpty(val) && map.TryGetValue(val, out var mapped)) { obj[prop] = mapped; }
    }
}

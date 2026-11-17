/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

/// <summary>
/// SessionManager, file-history side: which files the CLI backed up before Claude changed them, read
/// from the records it writes into the transcript.
/// <para>Two kinds of record, measured on CLI 2.1.310. A <c>file-history-delta</c> is written the
/// first time a file is touched: its backup is the file as it was before that edit. A
/// <c>file-history-snapshot</c> is written for every user message and lists every file tracked so
/// far, each with the copy taken at the start of that turn. A later edit of an already tracked file
/// writes nothing at all, so the earliest record for a path is the one that holds the content from
/// before the session's first edit of it, and the latest is only "as of the last turn".</para>
/// <para>Edits made by a sub-agent are in neither: the CLI does not track them.</para>
/// </summary>
internal sealed partial class SessionManager
{
    // How much of the end of the transcript is searched for a delta while a tool call is fresh. The
    // record is written within a few hundred milliseconds of the hook; this only has to cover what
    // a busy turn writes in between, a few large tool results at worst.
    private const int FreshTailBytes = 1024 * 1024;

    /// <summary>One backup the CLI recorded.</summary>
    public sealed class FileHistoryEntry
    {
        /// <summary>As the CLI spells it: relative to the working directory when under it, absolute otherwise.</summary>
        public string TrackingPath { get; set; }

        /// <summary>The directory the file really lives in, links resolved.</summary>
        public string RealParentDir { get; set; }

        /// <summary>The copy under <c>file-history/&lt;session&gt;/</c>. Null when the file did not
        /// exist at that point: the backup is "nothing".</summary>
        public string BackupFileName { get; set; }

        public int Version { get; set; }
    }

    /// <summary>Every record for one file: the first and the last in transcript order.</summary>
    public sealed class FileHistoryTrack
    {
        public FileHistoryEntry Earliest { get; set; }
        public FileHistoryEntry Latest { get; set; }
    }

    /// <summary>The CLI's backup file for an entry, or null when it names none.</summary>
    public string BackupPath(string sessionId, FileHistoryEntry entry)
        => entry?.BackupFileName is null || !IsSafePathToken(sessionId)
            ? null
            : Path.Combine(paths.FileHistoryFolder, sessionId, entry.BackupFileName);

    /// <summary>Every file the CLI has backed up in a session, by absolute path. One pass over the
    /// transcript, so it is for a resume, not for each edit: see <see cref="FindRecentDelta"/>.</summary>
    public async Task<Dictionary<string, FileHistoryTrack>> ReadFileHistoryAsync(string sessionId, PaneRoot root = null)
    {
        var tracks = new Dictionary<string, FileHistoryTrack>(StringComparer.OrdinalIgnoreCase);
        var path = FileFor(sessionId);
        if (path is null) { return tracks; }
        try
        {
            // Opened shared: the CLI may be appending to the transcript while a resume reads it, and File.ReadLines, which is not,
            // would read nothing at all when it races the CLI's own writes.
            using var stream = await AsyncFile.OpenReadAsync(path).ConfigureAwait(false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            string line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) { continue; }
                // IsType rather than a literal match: a writer that pretty-prints is valid JSON too.
                var delta = IsType(line, "file-history-delta");
                if (!delta && !IsType(line, "file-history-snapshot")) { continue; }
                JObject obj;
                try { obj = JObject.Parse(line); }
                catch (Newtonsoft.Json.JsonException) { continue; }

                foreach (var entry in delta ? DeltaEntries(obj) : SnapshotEntries(obj))
                {
                    var key = ChangePaths.Resolve(entry.TrackingPath, entry.RealParentDir, workingDirectory, root);
                    if (key is null) { continue; }
                    if (!tracks.TryGetValue(key, out var track))
                    {
                        track = new FileHistoryTrack { Earliest = entry };
                        tracks[key] = track;
                    }
                    track.Latest = entry;
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No transcript yet: nothing was tracked.
        }
        catch (Exception ex) { log.LogException("SessionManager.ReadFileHistory", ex); }
        return tracks;
    }

    /// <summary>The newest delta in the transcript tail that is about <paramref name="hookPath"/>.
    /// Reads the transcript the hook named, not one derived from the session id: the CLI says where
    /// it is writing, and that cannot be wrong; the session id is only the fallback for a CLI that
    /// names none. Null when there is no such record yet.</summary>
    public async Task<FileHistoryEntry> FindRecentDeltaAsync(string sessionId, string transcriptPath, string hookPath, PaneRoot root = null)
    {
        try
        {
            var path = string.IsNullOrEmpty(transcriptPath) ? FileFor(sessionId) : LongPath(transcriptPath);
            if (path is null) { return null; }
            var tail = await ReadTailLinesAsync(path, FreshTailBytes).ConfigureAwait(false);
            foreach (var line in tail.AsEnumerable().Reverse())
            {
                if (!IsType(line, "file-history-delta")) { continue; }
                JObject obj;
                try { obj = JObject.Parse(line); }
                catch (Newtonsoft.Json.JsonException) { continue; }
                foreach (var entry in DeltaEntries(obj))
                {
                    if (ChangePaths.Matches(hookPath, entry.TrackingPath, entry.RealParentDir, workingDirectory, root)) { return entry; }
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The CLI has not created the transcript yet; the caller asks again.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The CLI may be mid-write; the caller asks again.
            log.Debug(() => $"[changes] transcript tail unreadable: {ex.Message}");
        }
        return null;
    }

    private static IEnumerable<FileHistoryEntry> DeltaEntries(JObject obj)
    {
        var trackingPath = obj.Val("trackingPath", "");
        if (trackingPath.Length == 0 || obj["backup"] is not JObject backup) { yield break; }
        yield return ToEntry(trackingPath, backup);
    }

    private static IEnumerable<FileHistoryEntry> SnapshotEntries(JObject obj)
    {
        if (obj["snapshot"]?["trackedFileBackups"] is not JObject tracked) { yield break; }
        foreach (var kv in tracked)
        {
            if (kv.Value is JObject backup) { yield return ToEntry(kv.Key, backup); }
        }
    }

    private static FileHistoryEntry ToEntry(string trackingPath, JObject backup) => new()
    {
        TrackingPath = trackingPath,
        RealParentDir = backup.Val("realParentDir", (string)null),
        BackupFileName = backup.Val("backupFileName", (string)null),
        Version = backup.Val("version", 0),
    };

    /// <summary>The last whole lines in the final <paramref name="maxBytes"/> of a file, oldest first.
    /// The first line is dropped when the window starts inside it, so a cut line is never parsed. The
    /// file is opened shared: the CLI is appending to it.</summary>
    internal static async Task<List<string>> ReadTailLinesAsync(string path, int maxBytes)
    {
        using var stream = await AsyncFile.OpenReadAsync(path).ConfigureAwait(false);
        var length = stream.Length;
        var take = (int)Math.Min(length, maxBytes);
        stream.Seek(length - take, SeekOrigin.Begin);
        var buffer = new byte[take];
        var read = await AsyncFile.FillAsync(stream, buffer).ConfigureAwait(false);

        // A newline byte never occurs inside a multi-byte UTF-8 sequence, so decoding from the middle
        // of one only garbles the first line, which is dropped below.
        var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n').ToList();
        if (length > take && lines.Count > 0) { lines.RemoveAt(0); }
        lines.RemoveAll(string.IsNullOrWhiteSpace);
        return lines;
    }
}

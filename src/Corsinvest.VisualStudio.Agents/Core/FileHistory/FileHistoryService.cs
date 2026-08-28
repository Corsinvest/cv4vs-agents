/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.FileHistory;

/// <summary>
/// Enumerates what the CLI's file-history occupies on disk, per config-dir.
/// <para><b>Not built on StatsService.</b> The stats tree answers "where did I work" over every
/// session in `projects/`; this one answers "what takes up space and can be deleted", and only
/// sessions with a `file-history/&lt;id&gt;/` folder have any. On a real config-dir that is a few
/// dozen against a couple of thousand, so reusing the indexed tree would mean building all of it to
/// then prune it — and it could never show an ORPHAN, a backup folder whose transcript is gone,
/// because a tree built from `projects/` has no node for a session that no longer has a .jsonl.</para>
/// <para>No indexer and no cache: the sizes come from the filesystem, which already knows them.
/// The per-file detail (which real path each hashed copy belongs to) lives in the transcript and is
/// read on demand, one session at a time — see <see cref="Sessions.SessionManager.ReadAllFileBackups"/>.</para>
/// </summary>
internal static class FileHistoryService
{
    /// <summary>One session's backup folder: what it costs and what it belongs to.</summary>
    internal sealed class SessionBackups
    {
        public string SessionId { get; set; }

        /// <summary>`~/.claude/file-history/&lt;session&gt;`.</summary>
        public string Folder { get; set; }

        /// <summary>The project folder under `projects/` whose transcript names this session, or
        /// null when nothing does — an orphan, kept and shown rather than hidden.</summary>
        public string ProjectDir { get; set; }

        /// <summary>Copies on disk. A file edited over several turns has one per version.</summary>
        public int CopyCount { get; set; }

        /// <summary>Distinct files behind those copies — the `@vN` suffix stripped. Counted from the
        /// names, so it holds even when the transcript is gone.</summary>
        public int FileCount { get; set; }

        public long Bytes { get; set; }

        /// <summary>Newest copy in the folder. Local time, for a column the user reads.</summary>
        public DateTime LastWrite { get; set; }

        public bool IsOrphan => ProjectDir == null;
    }

    /// <summary>Every session backup under one config-dir, largest first.</summary>
    internal sealed class ConfigDirBackups
    {
        public ClaudePaths Paths { get; set; }

        /// <summary>The profiles resolving to this config-dir — several may share one, which is why
        /// the tree is rooted on the directory and not on the profile.</summary>
        public List<Profile> Profiles { get; } = [];

        public List<SessionBackups> Sessions { get; } = [];

        public long Bytes => Sessions.Sum(s => s.Bytes);
    }

    /// <summary>Scan every config-dir in use. Pure filesystem: directory listings and file lengths,
    /// no transcript is opened, so this is fast enough to run on the UI thread's behalf without a
    /// progress indicator — but the caller still does it off-thread, since a config-dir may sit on
    /// a network share.</summary>
    public static List<ConfigDirBackups> Scan()
    {
        var result = new List<ConfigDirBackups>();
        try
        {
            // Both lists, like StatsService: `false` drops disabled profiles, `true` adds them back.
            // A profile that is merely switched off still owns the disk its backups occupy.
            var byConfig = new Dictionary<string, ConfigDirBackups>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in ProfileStore.Load(forEdit: false).Concat(ProfileStore.Load(forEdit: true)))
            {
                var paths = ClaudePaths.ForProfile(profile);
                if (!byConfig.TryGetValue(paths.ConfigId, out var entry))
                {
                    entry = new ConfigDirBackups { Paths = paths };
                    byConfig[paths.ConfigId] = entry;
                    ScanConfigDir(entry);
                }
                if (!entry.Profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal)))
                {
                    entry.Profiles.Add(profile);
                }
            }
            result.AddRange(byConfig.Values.OrderByDescending(c => c.Bytes));
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException(nameof(FileHistoryService) + ".Scan", ex); }
        return result;
    }

    private static void ScanConfigDir(ConfigDirBackups entry)
    {
        var root = entry.Paths.FileHistoryFolder;
        if (!Directory.Exists(root)) { return; }

        // Session id → project folder, from the .jsonl NAMES alone. Opening none of them is the
        // point: a config-dir holds thousands, and the name is all this mapping needs.
        var owners = ProjectsBySessionId(entry.Paths);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var id = Path.GetFileName(dir);
                var files = new DirectoryInfo(dir).GetFiles();
                var session = new SessionBackups
                {
                    SessionId = id,
                    Folder = dir,
                    ProjectDir = owners.TryGetValue(id, out var owner) ? owner : null,
                    CopyCount = files.Length,
                    FileCount = files.Select(StripVersion).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Bytes = files.Sum(f => f.Length),
                    LastWrite = files.Length == 0 ? Directory.GetLastWriteTime(dir) : files.Max(f => f.LastWriteTime),
                };
                entry.Sessions.Add(session);
            }
            catch (Exception ex) { OutputWindowLogger.Global.LogException(nameof(FileHistoryService) + ".ScanSession", ex); }
        }

        entry.Sessions.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
    }

    /// <summary>The copies are named `&lt;hash-of-path&gt;@v&lt;n&gt;`, so dropping the suffix
    /// groups every version of one file together. A name without one counts as its own file rather
    /// than being skipped — the naming is the CLI's, and it is free to change it.</summary>
    private static string StripVersion(FileInfo file)
    {
        var name = file.Name;
        var at = name.LastIndexOf('@');
        return at > 0 ? name.Substring(0, at) : name;
    }

    private static Dictionary<string, string> ProjectsBySessionId(ClaudePaths paths)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(paths.ProjectsFolder)) { return map; }
        try
        {
            foreach (var projectDir in Directory.EnumerateDirectories(paths.ProjectsFolder))
            {
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
                {
                    map[Path.GetFileNameWithoutExtension(file)] = projectDir;
                }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException(nameof(FileHistoryService) + ".ProjectsBySessionId", ex); }
        return map;
    }

    /// <summary>Delete one session's backup folder. Returns false and logs when it fails — a copy
    /// held open by a running CLI is the case that actually happens.</summary>
    public static bool Delete(SessionBackups session)
    {
        try
        {
            Directory.Delete(session.Folder, recursive: true);
            OutputWindowLogger.Global.Info($"[filehistory] deleted backups of {session.SessionId} ({Format.Size(session.Bytes)})");
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException(nameof(FileHistoryService) + ".Delete", ex);
            return false;
        }
    }

    /// <summary>Session ids currently driven by an open pane. Their backups are what a rewind in
    /// that pane would restore from, so the UI refuses to delete them.</summary>
    public static HashSet<string> LiveSessionIds()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Panes.PaneRegistry.Instance.Entries)
        {
            if (!string.IsNullOrEmpty(e.ActiveSessionId)) { live.Add(e.ActiveSessionId); }
        }
        return live;
    }

    /// <summary>Sizes and counts as the pane prints them. Here rather than in the control: the
    /// confirmation dialog and the tree have to agree on what a number looks like.</summary>
    internal static class Format
    {
        public static string Size(long bytes)
            => bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.0} GB"
             : bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024:0.0} MB"
             : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
             : $"{bytes} B";
    }
}

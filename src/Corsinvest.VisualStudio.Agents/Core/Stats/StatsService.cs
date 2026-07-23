/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Corsinvest.VisualStudio.Agents.Core.Profiles;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Scope of the aggregation, matching the tree levels: everything across profiles, one
/// profile's config-dir, a folder in the path tree (every project beneath it), one project, one
/// calendar day of a project, or a single session.</summary>
internal enum StatsScope { All, Profile, Folder, Project, Day, Session }

/// <summary>Time range filter (applied to the per-day buckets).</summary>
internal enum StatsRange { All, Last30d, Last7d }

/// <summary>
/// Computes usage statistics over a scope+range from the local .jsonl, backed by the per-project
/// incremental cache. Heavy on first run ("All" reads every project once); call it off the UI
/// thread. Subsequent runs re-read only files whose mtime/size changed (the current session's
/// delta), so they're fast.
/// </summary>
internal static class StatsService
{
    // Single in-flight indexing pass at a time. We keep NO data or Task in memory — just this
    // flag (0 = idle, 1 = running). The heavy work (reading every .jsonl → per-project cache on
    // disk) runs once; concurrent callers see IsIndexing and read the current cache instead of
    // launching a duplicate pass. Reading is always cache-only (Aggregate), never in RAM.
    private static int _indexing;

    /// <summary>True while a full indexing pass is running (the dialog shows a loading bar and
    /// re-reads when it flips back to false).</summary>
    public static bool IsIndexing => System.Threading.Volatile.Read(ref _indexing) != 0;

    /// <summary>Fires when an indexing pass finishes (cache is now up to date) so the UI can
    /// re-read. Raised on the indexing thread; handlers must marshal to the UI thread.</summary>
    public static event Action IndexingCompleted;

    /// <summary>Run one full indexing pass over EVERY profile (the WPF tree spans all profiles).
    /// Single-flight; no-op (false) if a pass is already in flight. Refreshes each project's on-disk
    /// cache; holds no result in memory. force=true ignores the existing cache and re-reads every
    /// .jsonl from scratch (the Refresh button — picks up moved files / changed cwd).</summary>
    public static bool StartIndexing(bool force = false)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _indexing, 1, 0) != 0) { return false; }
        Task.Run(() =>
        {
            try
            {
                foreach (var profile in ProfileStore.Load(forEdit: false))
                {
                    IndexAllProjects(workingDirectory: null, ClaudePaths.ForProfile(profile), force);
                }
            }
            catch (Exception ex) { OutputWindowLogger.LogException(nameof(StatsService) + ".Index", ex); }
            finally
            {
                System.Threading.Volatile.Write(ref _indexing, 0);
                IndexingCompleted?.Invoke();
            }
        });
        return true;
    }

    /// <summary>Run one full indexing pass for a SINGLE profile (current workspace first), unless
    /// one is already running. Used by the pane's WebView dialog, which is single-profile.</summary>
    public static bool StartIndexing(string workingDirectory, ClaudePaths paths)
    {
        // CompareExchange: only the winner runs; everyone else bails (single-flight).
        if (System.Threading.Interlocked.CompareExchange(ref _indexing, 1, 0) != 0) { return false; }
        Task.Run(() =>
        {
            // paths is immutable, so capturing it in the closure is safe.
            try { IndexAllProjects(workingDirectory, paths); }
            catch (Exception ex) { OutputWindowLogger.LogException(nameof(StatsService) + ".Index", ex); }
            finally
            {
                System.Threading.Volatile.Write(ref _indexing, 0);
                IndexingCompleted?.Invoke();
            }
        });
        return true;
    }

    /// <summary>Profiles collapsed by config-dir: two profiles (e.g. Claude + a GLM override) can
    /// share the same ~/.claude, hence the same .jsonl. They'd otherwise be double-counted in All
    /// and appear as duplicate tree nodes. Returns one entry per distinct config-dir: a representative
    /// profile (any of the group — same paths) and a label joining every profile name that uses it.</summary>
    private static IEnumerable<(Profile profile, string label)> DistinctConfigs()
    {
        return ProfileStore.Load(forEdit: false)
            .GroupBy(p => ClaudePaths.ForProfile(p).ConfigId, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                profile: g.First(),
                label: string.Join(", ", g.Select(p => string.IsNullOrEmpty(p.Name) ? "(profile)" : p.Name))));
    }

    /// <summary>Build the navigation tree (All → Profile → Folder… → Project → Day → Session),
    /// reading only the filesystem/cache — no .jsonl parse. The range hides sessions (and the days /
    /// projects / folders that become empty) worked outside it; "All time" shows everything. Every
    /// node carries the <see cref="StatsSelection"/> its level aggregates.</summary>
    public static StatsTreeNode BuildTree(StatsRange range = StatsRange.All)
    {
        // Sessions whose last activity is before this are hidden (DateTime.MinValue = show all).
        var minDay = range switch
        {
            StatsRange.Last7d => DateTime.Now.Date.AddDays(-6),
            StatsRange.Last30d => DateTime.Now.Date.AddDays(-29),
            _ => DateTime.MinValue,
        };

        var root = new StatsTreeNode
        {
            Label = "All",
            Selection = new StatsSelection { Scope = StatsScope.All },
        };

        foreach (var (profile, profileLabel) in DistinctConfigs())
        {
            var pNode = new StatsTreeNode
            {
                Label = profileLabel,
                Selection = new StatsSelection { Scope = StatsScope.Profile, Profile = profile },
            };
            root.Children.Add(pNode);

            var paths = ClaudePaths.ForProfile(profile);
            if (!Directory.Exists(paths.ProjectsFolder)) { continue; }

            var projects = Directory.EnumerateDirectories(paths.ProjectsFolder)
                .Select(dir => { var info = ProjectCacheInfo(paths, dir); return (dir, info.cwd, info.hasData, info.lastActivity, label: ProjectLabel(dir, info.cwd)); })
                // Show only projects with confirmed activity in the cache. This also drops the
                // not-yet-indexed ones (no cwd → ugly encoded name); the Refresh pass fills them in.
                .Where(x => x.hasData == true)
                .ToList();

            BuildFolderTree(pNode, profile, projects, minDay);
        }
        return root;
    }

    // A mutable folder node while the path tree is being built (before conversion to StatsTreeNode).
    private sealed class FolderBuild
    {
        public string Segment;                    // this folder's own name (one path segment)
        public string FullPath;                   // full path down to here (for the folder scope)
        public readonly SortedDictionary<string, FolderBuild> Sub =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string dir, string cwd, Dictionary<string, DateTime> lastActivity, string label)> Projects =
            new();
    }

    // Build a real nested folder tree from the projects' working directories, then attach it under
    // the profile node. Single-child folder chains are collapsed (…\source\repos shows as one node
    // until it branches), and every folder node is clickable — it aggregates every project beneath it.
    private static void BuildFolderTree(StatsTreeNode profileNode, Profile profile,
        List<(string dir, string cwd, bool? hasData, Dictionary<string, DateTime> lastActivity, string label)> projects,
        DateTime minDay)
    {
        var root = new FolderBuild { Segment = "", FullPath = "" };
        foreach (var p in projects)
        {
            // The cwd's last segment is the project itself; the ones before it are folders.
            var segs = SplitPath(p.cwd);
            var node = root;
            for (var i = 0; i < segs.Count - 1; i++)
            {
                if (!node.Sub.TryGetValue(segs[i], out var child))
                {
                    child = new FolderBuild
                    {
                        Segment = segs[i],
                        FullPath = node.FullPath.Length == 0 ? segs[i] : node.FullPath + "\\" + segs[i],
                    };
                    node.Sub[segs[i]] = child;
                }
                node = child;
            }
            node.Projects.Add((p.dir, p.cwd, p.lastActivity, p.label));
        }

        foreach (var child in root.Sub.Values)
        {
            var folderNode = ToFolderNode(child, profile, minDay);
            if (folderNode != null) { profileNode.Children.Add(folderNode); }
        }
    }

    // Convert a FolderBuild to a StatsTreeNode, collapsing single-child chains (a folder with one
    // subfolder and no projects of its own merges its label with the child, like a solution tree).
    // Returns null when the whole subtree is empty in the range (nothing to show).
    private static StatsTreeNode ToFolderNode(FolderBuild fb, Profile profile, DateTime minDay)
    {
        // Collapse: while this folder holds exactly one subfolder and no direct projects, fold the
        // child up into it so "…\source\repos\Clienti" reads as one node until it branches.
        var label = fb.Segment;
        while (fb.Projects.Count == 0 && fb.Sub.Count == 1)
        {
            var only = fb.Sub.Values.First();
            label = label + "\\" + only.Segment;
            fb = only;
        }

        var children = new List<StatsTreeNode>();
        foreach (var sub in fb.Sub.Values)
        {
            var subNode = ToFolderNode(sub, profile, minDay);
            if (subNode != null) { children.Add(subNode); }
        }
        foreach (var p in fb.Projects.OrderBy(p => p.label, StringComparer.OrdinalIgnoreCase))
        {
            var prjNode = BuildProjectNode(profile, p.dir, p.cwd, p.label, p.lastActivity, minDay);
            if (prjNode != null) { children.Add(prjNode); }
        }
        if (children.Count == 0) { return null; } // nothing in range under this folder

        var node = new StatsTreeNode
        {
            Label = label,
            Tooltip = fb.FullPath,
            Selection = new StatsSelection
            {
                Scope = StatsScope.Folder,
                Profile = profile,
                ProjectDirs = CollectProjectDirs(fb),
            },
        };
        node.Children.AddRange(children);
        return node;
    }

    // Every project dir at or below this folder (for the Folder scope's aggregation).
    private static List<string> CollectProjectDirs(FolderBuild fb)
    {
        var dirs = new List<string>();
        void Walk(FolderBuild n)
        {
            foreach (var p in n.Projects) { dirs.Add(p.dir); }
            foreach (var s in n.Sub.Values) { Walk(s); }
        }
        Walk(fb);
        return dirs;
    }

    // Split a path into segments, keeping the drive/root as the first one (K:\a\b → [K:, a, b]).
    private static List<string> SplitPath(string path)
    {
        if (string.IsNullOrEmpty(path)) { return new List<string> { "(unknown)" }; }
        return path.Replace('/', '\\').TrimEnd('\\')
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    // The Project subtree: a "Days" branch (real per-day tokens, matching the chart) and a
    // "Sessions" branch (whole files, titled). Both are clickable (= the project total). Returns
    // null when nothing remains in the range.
    private static StatsTreeNode BuildProjectNode(Profile profile, string projectDir, string cwd,
        string label, Dictionary<string, DateTime> lastActivity, DateTime minDay)
    {
        var paths = ClaudePaths.ForProfile(profile);

        // Days: the project's real active days (from the cache's per-day buckets), in range, newest
        // first. Each Day aggregates that calendar day's tokens across every session.
        var days = ProjectActiveDays(paths, projectDir)
            .Where(d => DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) && dt.Date >= minDay)
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .ToList();

        // Sessions: one per .jsonl (whole file), placed by last activity, titled via SessionManager
        // (realtime — no stale cached title). Range filter on last-activity day.
        var titles = SessionTitles(paths, cwd);
        var sessions = new DirectoryInfo(projectDir)
            .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(f => (id: SessionIdOf(f.Name),
                when: lastActivity.TryGetValue(SessionIdOf(f.Name), out var la) ? la : f.CreationTime))
            .Where(s => s.when.Date >= minDay)
            .OrderByDescending(s => s.when)
            .ToList();

        if (days.Count == 0 && sessions.Count == 0) { return null; }

        var prjNode = new StatsTreeNode
        {
            Label = label,
            Tooltip = cwd,
            Selection = new StatsSelection { Scope = StatsScope.Project, Profile = profile, ProjectDir = projectDir },
        };

        if (days.Count > 0)
        {
            var daysNode = new StatsTreeNode
            {
                Label = $"Days ({days.Count})",
                Kind = StatsNodeKind.DaysGroup,
                // Clicking the container = the whole project.
                Selection = new StatsSelection { Scope = StatsScope.Project, Profile = profile, ProjectDir = projectDir },
            };
            prjNode.Children.Add(daysNode);
            foreach (var d in days)
            {
                var when = DateTime.Parse(d, CultureInfo.InvariantCulture);
                daysNode.Children.Add(new StatsTreeNode
                {
                    Label = when.ToString("d", CultureInfo.CurrentCulture),
                    Selection = new StatsSelection { Scope = StatsScope.Day, Profile = profile, ProjectDir = projectDir, Date = d },
                });
            }
        }

        if (sessions.Count > 0)
        {
            var sessionsNode = new StatsTreeNode
            {
                Label = $"Sessions ({sessions.Count})",
                Kind = StatsNodeKind.SessionsGroup,
                Selection = new StatsSelection { Scope = StatsScope.Project, Profile = profile, ProjectDir = projectDir },
            };
            prjNode.Children.Add(sessionsNode);
            foreach (var s in sessions)
            {
                titles.TryGetValue(s.id, out var title);
                var time = s.when.ToString("g", CultureInfo.CurrentCulture);
                sessionsNode.Children.Add(new StatsTreeNode
                {
                    Label = string.IsNullOrEmpty(title) ? time : title,
                    Tooltip = string.IsNullOrEmpty(title) ? null : $"{time} — {title}",
                    Selection = new StatsSelection
                    {
                        Scope = StatsScope.Session,
                        Profile = profile,
                        ProjectDir = projectDir,
                        SessionIds = new List<string> { s.id },
                    },
                });
            }
        }
        return prjNode;
    }

    // The project's active calendar days (yyyy-MM-dd) from the cache's per-day buckets (days with
    // messages). Read-only.
    private static IEnumerable<string> ProjectActiveDays(ClaudePaths paths, string projectDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var cache = StatsCache.Load(CacheFileFor(paths, projectDir));
            foreach (var kv in cache)
            {
                var agg = kv.Value.Aggregate;
                if (agg == null) { continue; }
                foreach (var day in agg.Days)
                {
                    if (day.Value.MessageCount > 0) { seen.Add(day.Key); }
                }
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException(nameof(StatsService) + ".ProjectActiveDays", ex); }
        return seen;
    }

    // sessionId → title, read live via SessionManager (custom/ai title or last prompt). Empty when
    // the working directory is unknown (no cwd yet) or the folder is missing.
    private static Dictionary<string, string> SessionTitles(ClaudePaths paths, string cwd)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(cwd)) { return map; }
        try
        {
            foreach (var s in new Sessions.SessionManager(paths, cwd).Load())
            {
                if (s.Id != null && !string.IsNullOrEmpty(s.Title)) { map[s.Id] = s.Title; }
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException(nameof(StatsService) + ".SessionTitles", ex); }
        return map;
    }

    /// <summary>A readable label for a CLI project dir. The dir name is a LOSSY encoding of the real
    /// path (every non-alphanumeric char → '-'), so it can't be decoded reliably. Instead use the
    /// captured Cwd (the real working directory) and take its leaf folder name. Falls back to the
    /// raw dir name when there's no Cwd yet (un-indexed / old cache).</summary>
    private static string ProjectLabel(string projectDir, string cwd)
    {
        if (!string.IsNullOrEmpty(cwd))
        {
            var leaf = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(leaf)) { return leaf; }
        }
        return Path.GetFileName(projectDir.TrimEnd('\\', '/'));
    }

    /// <summary>Read-only cache probe for a project: its working directory (the projectCwd stored at
    /// the cache root), whether it has any real activity, and each main session's last-activity time
    /// (for grouping sessions by the day worked, not the file's creation day). hasData is null when
    /// nothing is cached yet, true when a cached aggregate has messages, false when it has none.</summary>
    private static (string cwd, bool? hasData, Dictionary<string, DateTime> lastActivity)
        ProjectCacheInfo(ClaudePaths paths, string projectDir)
    {
        var lastActivity = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cacheFile = CacheFileFor(paths, projectDir);
            var cache = StatsCache.Load(cacheFile);
            if (cache.Count == 0) { return (null, null, lastActivity); } // not indexed yet
            var cwd = StatsCache.LoadProjectCwd(cacheFile); // stored once at the cache root
            var hasData = false;
            foreach (var kv in cache)
            {
                var agg = kv.Value.Aggregate;
                if (agg == null) { continue; }
                if (agg.Messages > 0) { hasData = true; }
                if (!agg.IsSubagent && agg.LastTimestampMs > 0 && agg.SessionId != null)
                {
                    lastActivity[agg.SessionId] = FromUnixMs(agg.LastTimestampMs);
                }
            }
            return (cwd, hasData, lastActivity);
        }
        catch (Exception ex) { OutputWindowLogger.LogException(nameof(StatsService) + ".ProjectCacheInfo", ex); }
        return (null, null, lastActivity);
    }

    private static DateTime FromUnixMs(long ms)
        => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();

    /// <summary>Refresh every project's cache. When a workspace is given, its project is done first
    /// so its stats are ready soonest; a null/empty workspace (the all-profiles pass) just does them
    /// all in folder order. Pure I/O + parse → per-project cache file; nothing kept in RAM.</summary>
    private static void IndexAllProjects(string workingDirectory, ClaudePaths paths, bool force = false)
    {
        // No workspace (all-profiles pass): no "current" project to front-load.
        var current = string.IsNullOrEmpty(workingDirectory) ? null : paths.SessionFolder(workingDirectory);
        var ordered = new List<string>();
        if (current != null && Directory.Exists(current)) { ordered.Add(current); }
        if (Directory.Exists(paths.ProjectsFolder))
        {
            foreach (var d in Directory.EnumerateDirectories(paths.ProjectsFolder))
            {
                if (!string.Equals(d, current, StringComparison.OrdinalIgnoreCase)) { ordered.Add(d); }
            }
        }
        foreach (var projectDir in ordered) { IndexProject(projectDir, paths, force); }
    }

    // The projectDir is the CLI's dir (source of .jsonl); the cache lives in OUR data folder,
    // keyed by config-id + the same project-hash (the projectDir's basename).
    private static string CacheFileFor(ClaudePaths paths, string projectDir)
        => AppPaths.ProjectProfileFileByHash(paths, Path.GetFileName(projectDir.TrimEnd('\\', '/')), "stats-cache.json");

    /// <summary>Refresh one project's cache: re-read changed files (delta for grown ones), prune
    /// vanished, persist. This is the heavy step; Aggregate below only reads the result.</summary>
    private static void IndexProject(string projectDir, ClaudePaths paths, bool force = false)
    {
        var cacheFile = CacheFileFor(paths, projectDir);
        // force: start from an empty cache so every file is re-read from scratch (not delta).
        var cache = force
            ? new Dictionary<string, StatsCache.Entry>(StringComparer.OrdinalIgnoreCase)
            : StatsCache.Load(cacheFile);
        var files = EnumerateSessionFiles(projectDir, ids: null);
        var updated = new ConcurrentDictionary<string, StatsCache.Entry>();
        // cwd of each freshly-read file (not the reused ones) — used to pick the project label.
        var freshCwd = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file =>
            {
                var key = RelativeKey(projectDir, file.FullName);
                cache.TryGetValue(key, out var cached);
                var (entry, cwd) = RefreshFile(file, cached);
                if (entry != null) { updated[key] = entry; }
                if (!string.IsNullOrEmpty(cwd)) { freshCwd[key] = cwd; }
            });
        var next = new Dictionary<string, StatsCache.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in updated) { next[kv.Key] = kv.Value; }

        // Project label = the most recent session's cwd (a moved older file keeps a stale cwd). The
        // cwd isn't persisted per file, so pick it among the files re-read this pass, ranked by their
        // last-activity time; if none were re-read, keep whatever cwd the cache already had.
        string projectCwd = StatsCache.LoadProjectCwd(cacheFile);
        long best = long.MinValue;
        foreach (var kv in freshCwd)
        {
            var ts = next.TryGetValue(kv.Key, out var e) ? e.Aggregate?.LastTimestampMs ?? 0 : 0;
            if (ts >= best) { projectCwd = kv.Value; best = ts; }
        }
        StatsCache.Save(cacheFile, next, projectCwd);
    }

    /// <summary>Aggregate then map to the wire DTO (streaks, %, favorite model computed here).</summary>
    public static Contracts.StatsResponse BuildResponse(StatsSelection sel, StatsRange range)
    {
        var t = Aggregate(sel, range);
        long totalTokens = t.TotalTokens;

        var models = t.ModelUsage
            .Select(kv => new Contracts.StatsModelDto
            {
                Model = kv.Key,
                InputTokens = kv.Value.InputTokens,
                OutputTokens = kv.Value.OutputTokens,
                CacheReadTokens = kv.Value.CacheReadTokens,
                CacheCreationTokens = kv.Value.CacheCreationTokens,
                Percentage = totalTokens > 0 ? (double)kv.Value.Total / totalTokens * 100.0 : 0,
            })
            .OrderByDescending(m => m.InputTokens + m.OutputTokens)
            .ToArray();

        var days = t.Days
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Contracts.StatsDayDto
            {
                Date = kv.Key,
                MessageCount = kv.Value.MessageCount,
                SessionCount = kv.Value.SessionCount,
                ToolCallCount = kv.Value.ToolCallCount,
            })
            .ToArray();

        var dailyModel = t.Days
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new Contracts.StatsDayModelDto
            {
                Date = kv.Key,
                // The stacked chart wants a meaningful per-model daily total — GrandTotal (incl. cache),
                // since raw input/output is tiny next to cache read/creation.
                TokensByModel = kv.Value.TokensByModel.ToDictionary(x => x.Key, x => x.Value.GrandTotal),
            })
            .ToArray();

        var tools = t.ToolCallsByName
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new Contracts.StatsToolDto { Name = kv.Key, Count = kv.Value })
            .ToArray();

        var (current, longest) = CalculateStreaks(t.Days.Keys);
        int peakHour = t.HourCounts.Count == 0 ? -1 : t.HourCounts.OrderByDescending(kv => kv.Value).First().Key;
        string favorite = models.Length > 0 ? models[0].Model : null;

        return new Contracts.StatsResponse
        {
            Indexing = IsIndexing,
            TotalSessions = t.TotalSessions,
            TotalMessages = t.TotalMessages,
            TotalTokens = totalTokens,
            ActiveDays = t.Days.Count,
            CurrentStreak = current,
            LongestStreak = longest,
            PeakHour = peakHour,
            FavoriteModel = favorite,
            ImageCount = t.ImageCount,
            FileCount = t.FileCount,
            SubagentSessions = t.SubagentFiles,
            SubagentTokens = t.SubagentTokens,
            ModelBreakdown = models,
            DailyActivity = days,
            DailyModelTokens = dailyModel,
            TopTools = tools,
        };
    }

    /// <summary>current = consecutive active days ending today; longest = max consecutive run.</summary>
    private static (int current, int longest) CalculateStreaks(IEnumerable<string> activeDateKeys)
    {
        var set = new HashSet<string>(activeDateKeys);
        if (set.Count == 0) { return (0, 0); }

        // current: walk back from today while the day is active.
        int current = 0;
        var day = DateTime.Now.Date;
        while (set.Contains(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
        {
            current++;
            day = day.AddDays(-1);
        }

        // longest: scan sorted dates for the max run of consecutive days.
        var sorted = set.OrderBy(d => d, StringComparer.Ordinal).ToList();
        int longest = 1, run = 1;
        for (int i = 1; i < sorted.Count; i++)
        {
            var prev = DateTime.ParseExact(sorted[i - 1], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var cur = DateTime.ParseExact(sorted[i], "yyyy-MM-dd", CultureInfo.InvariantCulture);
            run = (cur - prev).Days == 1 ? run + 1 : 1;
            if (run > longest) { longest = run; }
        }
        return (current, longest);
    }

    /// <summary>Aggregate for the given tree selection, reading ONLY the on-disk cache (no file
    /// re-read — that's StartIndexing's job). All folds every profile; Profile folds all of one
    /// profile's projects; Project one project; Day the sessions of one calendar day; Session one
    /// file. Returns whatever the cache holds, so an un-indexed project yields empty totals.</summary>
    public static StatsTotals Aggregate(StatsSelection sel, StatsRange range)
    {
        var (fromDate, toDate) = RangeBounds(range);

        // All: fold each DISTINCT config-dir once (profiles sharing a config-dir would double-count).
        if (sel.Scope == StatsScope.All)
        {
            var all = new StatsTotals();
            foreach (var (profile, _) in DistinctConfigs())
            {
                all.Merge(AggregateProfile(ClaudePaths.ForProfile(profile), fromDate, toDate));
            }
            return all;
        }

        var paths = ClaudePaths.ForProfile(sel.Profile);
        if (sel.Scope == StatsScope.Profile) { return AggregateProfile(paths, fromDate, toDate); }

        // Folder: fold every project beneath this folder in the path tree.
        if (sel.Scope == StatsScope.Folder)
        {
            var totals = new StatsTotals();
            foreach (var projectDir in sel.ProjectDirs ?? new List<string>())
            {
                AggregateProject(paths, projectDir, null, fromDate, toDate, totals);
            }
            return totals;
        }

        // Day: every session of the project, but only that one calendar day's tokens (a multi-day
        // session contributes just its slice) — narrow the date window to the day, keep all sessions.
        if (sel.Scope == StatsScope.Day)
        {
            return AggregateProject(paths, sel.ProjectDir, null, sel.Date, sel.Date, new StatsTotals());
        }

        // Session = one whole file (all its days); Project = every session, all days.
        var ids = sel.Scope == StatsScope.Session
            ? new HashSet<string>(sel.SessionIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
            : null;
        return AggregateProject(paths, sel.ProjectDir, ids, fromDate, toDate, new StatsTotals());
    }

    // Fold every project of one profile.
    private static StatsTotals AggregateProfile(ClaudePaths paths, string fromDate, string toDate)
    {
        var totals = new StatsTotals();
        if (!Directory.Exists(paths.ProjectsFolder)) { return totals; }
        foreach (var projectDir in Directory.EnumerateDirectories(paths.ProjectsFolder))
        {
            AggregateProject(paths, projectDir, null, fromDate, toDate, totals);
        }
        return totals;
    }

    // Merge one project's cached file aggregates into totals. ids = null aggregates every session;
    // a set narrows to those session ids (+ their subagents). Reads only the cache — never the .jsonl.
    private static StatsTotals AggregateProject(ClaudePaths paths, string projectDir, HashSet<string> ids,
        string fromDate, string toDate, StatsTotals totals)
    {
        if (!Directory.Exists(projectDir)) { return totals; }
        var cache = StatsCache.Load(CacheFileFor(paths, projectDir));
        var wanted = new HashSet<string>(
            EnumerateSessionFiles(projectDir, ids).Select(f => RelativeKey(projectDir, f.FullName)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var kv in cache)
        {
            if (!wanted.Contains(kv.Key)) { continue; }
            if (kv.Value.Aggregate != null) { Merge(totals, kv.Value.Aggregate, fromDate, toDate); }
        }
        return totals;
    }

    /// <summary>The .jsonl to aggregate in a project dir: main sessions (*.jsonl) + subagents
    /// (&lt;sid&gt;/subagents/agent-*.jsonl). ids = null takes every session; a set narrows to those
    /// session ids and their subagents.</summary>
    private static IEnumerable<FileInfo> EnumerateSessionFiles(string projectDir, HashSet<string> ids)
    {
        var dir = new DirectoryInfo(projectDir);
        var mains = dir.EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => ids == null || ids.Contains(SessionIdOf(f.Name)));

        var subs = dir.EnumerateDirectories()
            .Where(d => ids == null || ids.Contains(d.Name))
            .SelectMany(sid =>
            {
                var subDir = new DirectoryInfo(Path.Combine(sid.FullName, "subagents"));
                return subDir.Exists
                    ? subDir.EnumerateFiles("agent-*.jsonl", SearchOption.TopDirectoryOnly)
                    : [];
            });

        return mains.Concat(subs);
    }

    private static string SessionIdOf(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    private static string RelativeKey(string projectDir, string fullPath)
    {
        var rel = fullPath.Substring(projectDir.Length).TrimStart('\\', '/');
        return rel.Replace('\\', '/');
    }

    private static bool IsSubagentPath(string fullPath) =>
        fullPath.Replace('\\', '/').Contains("/subagents/");

    /// <summary>Re-aggregate a file only if changed: same mtime → reuse; grown (append) → delta
    /// from the cached size; shrunk/rewritten → recompute; new → full. Returns the fresh entry and
    /// the session's cwd (null when the file was reused unchanged — its cwd isn't cached).</summary>
    private static (StatsCache.Entry entry, string cwd) RefreshFile(FileInfo file, StatsCache.Entry cached)
    {
        long mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        long size = file.Length;
        bool isSub = IsSubagentPath(file.FullName);

        if (cached?.Aggregate != null && cached.Mtime == mtime && cached.Size == size)
        {
            return (cached, null); // unchanged
        }

        long fromOffset = 0;
        FileAggregate seed = null;
        if (cached?.Aggregate != null && size > cached.Size)
        {
            // Append-only growth: continue from where we left off.
            fromOffset = cached.Size;
            seed = cached.Aggregate;
        }

        var res = StatsAggregator.AggregateFile(file.FullName, isSub, fromOffset, seed);
        if (res == null) { return (cached, null); } // keep old on error
        var (agg, newSize, cwd) = res.Value;
        agg.SessionId ??= SessionIdOf(file.Name);
        return (new StatsCache.Entry { Mtime = mtime, Size = newSize, Aggregate = agg }, cwd);
    }

    private static void Merge(StatsTotals totals, FileAggregate f, string fromDate, string toDate)
    {
        bool ranged = fromDate != null || toDate != null;
        // A file is "in range" if any of its days falls in the range (or no range).
        bool fileInRange = !ranged || f.Days.Keys.Any(d => InRange(d, fromDate, toDate));

        // Sessions + peak hour: a session (non-subagent) counts once if in range.
        if (!f.IsSubagent)
        {
            if (fileInRange)
            {
                totals.TotalSessions++;
                if (f.FirstHour >= 0) { totals.HourCounts.TryGetValue(f.FirstHour, out var c); totals.HourCounts[f.FirstHour] = c + 1; }
            }
        }
        else if (fileInRange)
        {
            totals.SubagentFiles++;
            foreach (var m in f.ModelUsage.Values) { totals.SubagentTokens += m.Total; }
        }

        // Attachments + tool usage (whole-file, gated by range membership — the per-day split
        // isn't tracked for these, so range applies at file granularity).
        if (fileInRange)
        {
            totals.ImageCount += f.ImageCount;
            totals.FileCount += f.FileCount;
            foreach (var t in f.ToolCallsByName)
            {
                totals.ToolCallsByName.TryGetValue(t.Key, out var c);
                totals.ToolCallsByName[t.Key] = c + t.Value;
            }
        }

        // Per-day activity + messages, filtered by range.
        foreach (var kv in f.Days)
        {
            if (ranged && !InRange(kv.Key, fromDate, toDate)) { continue; }
            if (!totals.Days.TryGetValue(kv.Key, out var d)) { d = new DayActivity(); totals.Days[kv.Key] = d; }
            d.Add(kv.Value);
            // This file is one session; count it once per day it was active (subagents aren't
            // sessions). SessionCount isn't tracked per-day in the aggregate, so it's derived here.
            if (!f.IsSubagent)
            {
                d.SessionCount++;
                totals.TotalMessages += kv.Value.MessageCount;
            }
        }

        // Model usage. Unranged: exact per-model split from the file aggregate. Ranged: the same
        // split, now tracked per day+model, summed over the in-range days.
        if (!ranged)
        {
            foreach (var mk in f.ModelUsage)
            {
                if (!totals.ModelUsage.TryGetValue(mk.Key, out var mt)) { mt = new ModelTokens(); totals.ModelUsage[mk.Key] = mt; }
                mt.Add(mk.Value);
            }
        }
        else
        {
            foreach (var kv in f.Days)
            {
                if (!InRange(kv.Key, fromDate, toDate)) { continue; }
                foreach (var mk in kv.Value.TokensByModel)
                {
                    if (!totals.ModelUsage.TryGetValue(mk.Key, out var mt)) { mt = new ModelTokens(); totals.ModelUsage[mk.Key] = mt; }
                    mt.Add(mk.Value);
                }
            }
        }
    }

    private static bool InRange(string dateKey, string fromDate, string toDate)
    {
        if (fromDate != null && string.CompareOrdinal(dateKey, fromDate) < 0) { return false; }
        return toDate == null || string.CompareOrdinal(dateKey, toDate) <= 0;
    }

    private static (string fromDate, string toDate) RangeBounds(StatsRange range)
    {
        if (range == StatsRange.All) { return (null, null); }
        var today = DateTime.Now.Date;
        var days = range == StatsRange.Last7d ? 6 : 29; // inclusive of today
        var from = today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (from, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}

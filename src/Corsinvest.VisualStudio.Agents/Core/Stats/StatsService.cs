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

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Scope of the aggregation: the current session, the current project, or every project.</summary>
internal enum StatsScope { Session, Project, All }

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

    /// <summary>Run one full indexing pass (all projects, current workspace first) unless one is
    /// already running. Refreshes each project's on-disk cache; holds no result in memory. No-op
    /// (returns false) if a pass is already in flight.</summary>
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

    /// <summary>Refresh every project's cache (current workspace first, so its stats are ready
    /// soonest), then the rest. Pure I/O + parse → per-project cache file; nothing kept in RAM.</summary>
    private static void IndexAllProjects(string workingDirectory, ClaudePaths paths)
    {
        var current = paths.SessionFolder(workingDirectory);
        var ordered = new List<string>();
        if (Directory.Exists(current)) { ordered.Add(current); }
        if (Directory.Exists(paths.ProjectsFolder))
        {
            foreach (var d in Directory.EnumerateDirectories(paths.ProjectsFolder))
            {
                if (!string.Equals(d, current, StringComparison.OrdinalIgnoreCase)) { ordered.Add(d); }
            }
        }
        foreach (var projectDir in ordered) { IndexProject(projectDir, paths); }
    }

    // The projectDir is the CLI's dir (source of .jsonl); the cache lives in OUR data folder,
    // keyed by config-id + the same project-hash (the projectDir's basename).
    private static string CacheFileFor(ClaudePaths paths, string projectDir)
        => AppPaths.ProjectProfileFileByHash(paths, Path.GetFileName(projectDir.TrimEnd('\\', '/')), "stats-cache.json");

    /// <summary>Refresh one project's cache: re-read changed files (delta for grown ones), prune
    /// vanished, persist. This is the heavy step; Aggregate below only reads the result.</summary>
    private static void IndexProject(string projectDir, ClaudePaths paths)
    {
        var cacheFile = CacheFileFor(paths, projectDir);
        var cache = StatsCache.Load(cacheFile);
        var files = EnumerateSessionFiles(projectDir, StatsScope.All, null);
        var updated = new ConcurrentDictionary<string, StatsCache.Entry>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            file =>
            {
                var key = RelativeKey(projectDir, file.FullName);
                cache.TryGetValue(key, out var cached);
                var entry = RefreshFile(file, cached);
                if (entry != null) { updated[key] = entry; }
            });
        var next = new Dictionary<string, StatsCache.Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in updated) { next[kv.Key] = kv.Value; }
        StatsCache.Save(cacheFile, next);
    }

    /// <summary>Aggregate then map to the wire DTO (streaks, %, favorite model computed here).</summary>
    public static Contracts.StatsResponse BuildResponse(StatsScope scope, StatsRange range, string workingDirectory, string sessionId, ClaudePaths paths)
    {
        var t = Aggregate(scope, range, workingDirectory, sessionId, paths);
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

    /// <summary>Aggregate for the given scope, reading ONLY the on-disk cache (no file re-read —
    /// that's StartIndexing's job). Fast: it merges the cached per-file aggregates for the scope's
    /// projects and applies the range filter. workingDirectory = current workspace; sessionId =
    /// the open session (for Session scope). Returns whatever the cache currently holds, so an
    /// un-indexed project yields empty totals until the indexing pass has run.</summary>
    public static StatsTotals Aggregate(StatsScope scope, StatsRange range, string workingDirectory, string sessionId, ClaudePaths paths)
    {
        var projectDirs = ProjectDirsForScope(scope, workingDirectory, paths);
        var totals = new StatsTotals();
        var (fromDate, toDate) = RangeBounds(range);

        foreach (var projectDir in projectDirs)
        {
            var cache = StatsCache.Load(CacheFileFor(paths, projectDir));
            // Only the files in scope (Session scope narrows to the open session + its subagents).
            var wanted = new HashSet<string>(
                EnumerateSessionFiles(projectDir, scope, sessionId).Select(f => RelativeKey(projectDir, f.FullName)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var kv in cache)
            {
                if (!wanted.Contains(kv.Key)) { continue; }
                if (kv.Value.Aggregate != null) { Merge(totals, kv.Value.Aggregate, fromDate, toDate); }
            }
        }
        return totals;
    }

    private static IEnumerable<string> ProjectDirsForScope(StatsScope scope, string workingDirectory, ClaudePaths paths)
    {
        if (scope == StatsScope.All)
        {
            return !Directory.Exists(paths.ProjectsFolder)
                ? []
                : Directory.EnumerateDirectories(paths.ProjectsFolder);
        }
        // Session and Project both live under the current workspace's session folder.
        var dir = paths.SessionFolder(workingDirectory);
        return Directory.Exists(dir) ? new[] { dir } : Enumerable.Empty<string>();
    }

    /// <summary>The .jsonl to aggregate in a project dir: main sessions (*.jsonl) + subagents
    /// (&lt;sid&gt;/subagents/agent-*.jsonl). For Session scope, only the open session + its subagents.</summary>
    private static IEnumerable<FileInfo> EnumerateSessionFiles(string projectDir, StatsScope scope, string sessionId)
    {
        var dir = new DirectoryInfo(projectDir);
        var mains = dir.EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => scope != StatsScope.Session || SessionIdOf(f.Name) == sessionId);

        var subs = dir.EnumerateDirectories()
            .Where(d => scope != StatsScope.Session || d.Name == sessionId)
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
    /// from the cached size; shrunk/rewritten → recompute; new → full. Returns the fresh entry.</summary>
    private static StatsCache.Entry RefreshFile(FileInfo file, StatsCache.Entry cached)
    {
        long mtime = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        long size = file.Length;
        bool isSub = IsSubagentPath(file.FullName);

        if (cached?.Aggregate != null && cached.Mtime == mtime && cached.Size == size)
        {
            return cached; // unchanged
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
        if (res == null) { return cached; } // keep old on error
        var (agg, newSize) = res.Value;
        agg.SessionId ??= SessionIdOf(file.Name);
        return new StatsCache.Entry { Mtime = mtime, Size = newSize, Aggregate = agg };
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

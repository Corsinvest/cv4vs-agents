/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>
/// Incremental cache of file aggregates; cache file path is chosen by the caller (StatsService),
/// under our own data folder (never touches the CLI's files). One entry per .jsonl (keyed by
/// name relative to the project dir) holds its aggregate plus mtime+size so a grown append-only
/// file only re-reads its delta.
/// </summary>
internal static class StatsCache
{
    private const int Version = 6;

    /// <summary>One cached file: its aggregate + the mtime/size it was computed at.</summary>
    internal sealed class Entry
    {
        public long Mtime { get; set; }
        public long Size { get; set; }
        public FileAggregate Aggregate { get; set; }
    }

    /// <summary>The project's working directory, stored once at the cache root (the most recent
    /// session's cwd). Null on missing / version mismatch / not yet written.</summary>
    public static string LoadProjectCwd(string cacheFile)
    {
        if (!File.Exists(cacheFile)) { return null; }
        try
        {
            var root = JObject.Parse(File.ReadAllText(cacheFile));
            return root.Val("version", 0) != Version ? null : root.Val("projectCwd", (string)null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatsCache.LoadProjectCwd", ex);
            return null;
        }
    }

    /// <summary>Load the cache from a file (name → entry). Empty on missing / version
    /// mismatch / parse error (so a shape bump silently recomputes).</summary>
    public static Dictionary<string, Entry> Load(string cacheFile)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(cacheFile)) { return result; }
        try
        {
            var root = JObject.Parse(File.ReadAllText(cacheFile));
            if (root.Val("version", 0) != Version) { return result; }
            if (root["files"] is not JObject files) { return result; }
            foreach (var kv in files)
            {
                if (kv.Value is JObject e)
                {
                    result[kv.Key] = new Entry
                    {
                        Mtime = e.Val("mtime", 0L),
                        Size = e.Val("size", 0L),
                        Aggregate = e["aggregate"]?.ToObject<FileAggregate>(),
                    };
                }
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatsCache.Load", ex);
        }
        return result;
    }

    /// <summary>Persist the cache atomically (temp + replace) so a crash mid-write can't corrupt
    /// it. Concurrent writers (two panes on the same project) are last-write-wins — the aggregate
    /// is deterministic, so a re-write is idempotent.</summary>
    public static void Save(string cacheFile, Dictionary<string, Entry> entries, string projectCwd)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
            var root = new JObject
            {
                ["version"] = Version,
                ["projectCwd"] = projectCwd,
                ["files"] = new JObject(),
            };
            var files = (JObject)root["files"];
            foreach (var kv in entries)
            {
                files[kv.Key] = new JObject
                {
                    ["mtime"] = kv.Value.Mtime,
                    ["size"] = kv.Value.Size,
                    ["aggregate"] = kv.Value.Aggregate == null ? null : JObject.FromObject(kv.Value.Aggregate),
                };
            }
            var tmp = cacheFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(tmp, root.ToString(Formatting.None));
            if (File.Exists(cacheFile)) { File.Delete(cacheFile); }
            File.Move(tmp, cacheFile);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatsCache.Save", ex);
        }
    }
}

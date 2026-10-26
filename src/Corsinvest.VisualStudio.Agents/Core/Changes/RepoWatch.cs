/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Notices that something under a folder changed, and roughly what, without reading any of it.
/// <para>Deliberately a hint and not a record. It answers two questions cheaply: <em>has anything moved since
/// the last snapshot</em> (so a command that only reads costs nothing, and one that follows a quiet moment
/// needs no new snapshot) and <em>which paths were touched while a command ran</em> (so git is asked about
/// those and not about the whole tree). Git has the final word on what counts: it knows the ignore rules,
/// which this does not, so a build writing into <c>obj/</c> raises events here and is dropped there.</para>
/// <para>Events carry a sequence number. A <see cref="Mark"/> is "now"; everything after it is
/// <see cref="PathsSince"/> that mark. When the OS buffer overflows, or the list outgrows its cap, the
/// events are known to be incomplete (<see cref="OverflowedSince"/>) and the caller asks git instead.</para></summary>
internal sealed class RepoWatch : IDisposable
{
    // Past this many distinct paths the list is dropped and an overflow declared: a build or an install
    // touching this many files is a job for git, not for a list.
    private const int DefaultMaxPaths = 50_000;

    private static readonly string GitSegment = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;

    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, long> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly OutputWindowLogger _log;
    private readonly int _maxPaths;
    private long _sequence;
    private long _overflowedAt;

    public RepoWatch(string root, OutputWindowLogger log, int maxPaths = DefaultMaxPaths)
    {
        _log = log;
        _maxPaths = maxPaths;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            // The most the OS allows without a larger non-paged allocation: a burst of writes
            // is the case this exists for.
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Touch(e.FullPath);
        _watcher.Changed += (_, e) => Touch(e.FullPath);
        _watcher.Deleted += (_, e) => Touch(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Touch(e.OldFullPath);
            Touch(e.FullPath);
        };
        _watcher.Error += (_, e) =>
        {
            _log.Warn($"[changes] the file watcher lost events ({e.GetException().Message}): git will be asked instead");
            Interlocked.Exchange(ref _overflowedAt, Interlocked.Increment(ref _sequence));
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>"Now": the point <see cref="ChangedSince"/> and <see cref="PathsSince"/> count from.</summary>
    public long Mark() => Interlocked.Read(ref _sequence);

    /// <summary>Anything outside <c>.git</c> changed after the mark.</summary>
    public bool ChangedSince(long mark) => Interlocked.Read(ref _sequence) > mark;

    /// <summary>Events were lost after the mark, so <see cref="PathsSince"/> is a lower bound.</summary>
    public bool OverflowedSince(long mark) => Interlocked.Read(ref _overflowedAt) > mark;

    /// <summary>Every path (files and folders, created, changed, deleted or renamed) seen after the mark.</summary>
    public List<string> PathsSince(long mark)
        => _paths.Where(kv => kv.Value > mark).Select(kv => kv.Key).ToList();

    /// <summary>Feed an event in, as the watcher does. Public so a test can drive it without the disk.</summary>
    internal void Touch(string path)
    {
        if (string.IsNullOrEmpty(path) || IsInGit(path)) { return; }
        var sequence = Interlocked.Increment(ref _sequence);
        if (_paths.Count >= _maxPaths && !_paths.ContainsKey(path))
        {
            _paths.Clear();
            Interlocked.Exchange(ref _overflowedAt, sequence);
            return;
        }
        _paths[path] = sequence;
    }

    private static bool IsInGit(string path)
        => path.IndexOf(GitSegment, StringComparison.OrdinalIgnoreCase) >= 0
           || path.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _watcher.Dispose();
}

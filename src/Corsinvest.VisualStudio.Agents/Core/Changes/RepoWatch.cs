/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Notices that something under a folder changed, without reading any of it or keeping what.
/// <para>Deliberately a hint and not a record. It answers one question cheaply: <em>has anything moved since
/// the last snapshot</em>, so a command that only reads costs nothing and a quiet moment needs no new
/// snapshot. Which files changed, and how, is for git to say by comparing two snapshots: it knows the
/// ignore rules, the case of a name and what is a file, none of which a stream of events does, and an
/// event stream can be late or lose events when the OS buffer overflows.</para>
/// <para>A <see cref="Mark"/> is "now". Every event after it moves <see cref="ChangedSince"/> to true. Events
/// lost to an overflow count as a change: the answer is then "maybe", which is read as "yes".</para></summary>
internal sealed class RepoWatch : IDisposable
{
    // The folder inside a path, and the folder as the end of one.
    private static readonly string GitSegment = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}";
    private static readonly string GitTail = $"{Path.DirectorySeparatorChar}.git";

    // Visual Studio's own cache for a solution: rewritten constantly, never anybody's source, and
    // ignored by every Visual Studio .gitignore. Counting it would make the tree look busy forever.
    private static readonly string VsSegment = $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}";
    private static readonly string VsTail = $"{Path.DirectorySeparatorChar}.vs";

    private readonly FileSystemWatcher _watcher;
    private long _sequence;

    public RepoWatch(string root, OutputWindowLogger log)
    {
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
            log.Debug(() => $"[changes] the file watcher lost events ({e.GetException().Message}): taken as a change");
            Interlocked.Increment(ref _sequence);
        };
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>"Now": the point <see cref="ChangedSince"/> counts from.</summary>
    public long Mark() => Interlocked.Read(ref _sequence);

    /// <summary>Anything outside <c>.git</c> changed after the mark, or events were lost.</summary>
    public bool ChangedSince(long mark) => Interlocked.Read(ref _sequence) > mark;

    /// <summary>Feed an event in, as the watcher does. Internal so a test can drive it without the disk.</summary>
    internal void Touch(string path)
    {
        if (string.IsNullOrEmpty(path) || IsIgnored(path)) { return; }
        Interlocked.Increment(ref _sequence);
    }

    private static bool IsIgnored(string path)
        => path.IndexOf(GitSegment, StringComparison.OrdinalIgnoreCase) >= 0
           || path.IndexOf(VsSegment, StringComparison.OrdinalIgnoreCase) >= 0
           || path.EndsWith(GitTail, StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(VsTail, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _watcher.Dispose();
}

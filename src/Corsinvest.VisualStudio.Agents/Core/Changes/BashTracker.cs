/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Finds what a shell command changed, and what each file was before.
/// <para>The CLI's file history never sees a command's edits (<c>sed -i</c>, a script, <c>git checkout</c>),
/// so this keeps its own: a git snapshot of the working tree, taken whenever it has gone stale, is "before";
/// the files that changed while the command ran are "after". The snapshot is taken before the command
/// starts, because the CLI waits for the PreToolUse hook: one taken later could already hold the command's
/// own writes, and the change would read as no change at all.</para>
/// <para>Only a git working tree is covered. Elsewhere there is no ignore rule to tell source from build
/// output, so listing "everything a command wrote" would be mostly noise, and nothing to restore from.
/// Files outside the repo, or in a submodule, are left out for the same reason.</para></summary>
internal sealed class BashTracker : IDisposable
{
    // The watcher reports a moment after the command's last write.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    // More files than this from one command are not listed one by one: the rest are said to be missing.
    private const int MaxPerCall = 500;

    private const string LfsPointer = "version https://git-lfs.github.com/spec/v1";

    private sealed class Call
    {
        public long Mark;
        public string Tree;
    }

    private readonly GitSnapshot _git;
    private readonly RepoWatch _watch;
    private readonly ChangeSet _set;
    private readonly ChangeBaselines _baselines;
    private readonly OutputWindowLogger _log;
    private readonly ConcurrentDictionary<string, Call> _calls = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _snapshotMark = -1;

    private BashTracker(GitSnapshot git, RepoWatch watch, ChangeSet set, ChangeBaselines baselines, OutputWindowLogger log)
    {
        _git = git;
        _watch = watch;
        _set = set;
        _baselines = baselines;
        _log = log;
    }

    /// <summary>What the panel says when git is not installed.</summary>
    internal const string NoGitNote = "Files changed by running a command are not listed: git was not found.";

    /// <summary>What the panel says when the working directory is not in a git repository.</summary>
    internal const string NoRepositoryNote = "Files changed by running a command are not listed: this folder is not in a git repository.";

    /// <summary>What the panel says when the repository is a whole drive or the profile folder.</summary>
    internal const string TooBroadNote = "Files changed by running a command are not listed: the git repository here is your whole profile folder or drive.";

    /// <summary>Start looking after a working directory. Null when it is not in a git repository, or git
    /// is not there: nothing is then tracked and nothing is listed, and the set says why.</summary>
    public static async Task<BashTracker> StartAsync(string workingDirectory, string snapshotFolder, ChangeSet set,
                                                     ChangeBaselines baselines, OutputWindowLogger log, int? maxWatchedPaths = null)
    {
        var exe = GitExe.Resolve();
        if (exe is null)
        {
            log.Info("[changes] no git found: files changed by commands are not tracked");
            set.SetCommandsNote(NoGitNote);
            return null;
        }

        var snapshot = await GitSnapshot.OpenAsync(new GitRunner(exe, log), workingDirectory, snapshotFolder, log).ConfigureAwait(false);
        if (snapshot is null)
        {
            log.Info($"[changes] {workingDirectory} is not in a git repository: files changed by commands are not tracked");
            set.SetCommandsNote(NoRepositoryNote);
            return null;
        }

        if (GitSnapshot.IsTooBroad(snapshot.TopLevel))
        {
            log.Info($"[changes] the repository at {snapshot.TopLevel} is a whole drive or profile: files changed by commands are not tracked");
            snapshot.Dispose();
            set.SetCommandsNote(TooBroadNote);
            return null;
        }

        set.SetCommandsNote(null);
        var watch = maxWatchedPaths is int max ? new RepoWatch(snapshot.TopLevel, log, max) : new RepoWatch(snapshot.TopLevel, log);
        var tracker = new BashTracker(snapshot, watch, set, baselines, log);
        // The first picture, in the background: by the time a command arrives it is usually there, and
        // if it is not the command's own hook waits for it.
        _ = tracker.SnapshotAsync();
        return tracker;
    }

    /// <summary>The root of the working tree being watched.</summary>
    public string TopLevel => _git.TopLevel;

    /// <summary>A command is about to run. Brings the snapshot up to date first when anything has
    /// changed since it was taken, so it is a true "before"; a quiet working tree costs nothing here.</summary>
    public async Task BeginAsync(string toolUseId)
    {
        if (_git.LatestTree is null || _watch.ChangedSince(Interlocked.Read(ref _snapshotMark)))
        {
            await SnapshotAsync().ConfigureAwait(false);
        }
        _calls[toolUseId] = new Call { Mark = _watch.Mark(), Tree = _git.LatestTree };
    }

    /// <summary>A command has finished (or failed). Lists what it changed.</summary>
    public async Task EndAsync(string toolUseId, string sessionId)
    {
        if (!_calls.TryRemove(toolUseId, out var call)) { return; }
        await Task.Delay(SettleTime, _lifetime.Token).ConfigureAwait(false);

        var overflowed = _watch.OverflowedSince(call.Mark);
        var touched = _watch.PathsSince(call.Mark);
        if (touched.Count == 0 && !overflowed) { return; }

        var incomplete = false;
        var files = new List<string>();
        if (overflowed)
        {
            // Events were lost, so the watcher's list is a floor. Git has the whole answer: whatever
            // differs between the picture before the command and the one now.
            var after = await _git.TakeAsync(_lifetime.Token).ConfigureAwait(false);
            if (after is not null && call.Tree is not null)
            {
                files = (await _git.ChangedBetweenAsync(call.Tree, after).ConfigureAwait(false))
                    .Select(rel => Path.GetFullPath(Path.Combine(_git.TopLevel, rel))).ToList();
            }
            else
            {
                files = touched;
            }
            incomplete = true;
        }
        else
        {
            files = touched;
        }

        await RecordAsync(call, files, sessionId, incomplete).ConfigureAwait(false);

        // Their new content is the next command's "before".
        _ = SnapshotAsync();
    }

    private async Task RecordAsync(Call call, List<string> touched, string sessionId, bool incomplete)
    {
        // Files only, and only ones git would version: a folder is not a change, and what the rules ignore
        // (build output, packages) is not Claude's work.
        var candidates = new List<KeyValuePair<string, string>>();
        foreach (var path in touched.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path)) { continue; }
            var rel = _git.Relative(path);
            if (rel is not null) { candidates.Add(new KeyValuePair<string, string>(path, rel)); }
        }
        if (candidates.Count == 0) { return; }

        var ignored = await _git.IgnoredAsync(candidates.Select(c => c.Value).ToList()).ConfigureAwait(false);
        candidates = candidates.Where(c => !ignored.Contains(c.Value)).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count > MaxPerCall)
        {
            candidates = candidates.Take(MaxPerCall).ToList();
            incomplete = true;
        }
        if (candidates.Count == 0) { return; }

        var before = call.Tree is null
            ? new Dictionary<string, TreeBlob>()
            : await _git.ListBlobsAsync(call.Tree, candidates.Select(c => c.Value).ToList()).ConfigureAwait(false);

        foreach (var candidate in candidates)
        {
            if (_lifetime.IsCancellationRequested) { return; }
            await AddRowAsync(candidate.Key, candidate.Value, before, call.Tree is not null, sessionId, incomplete).ConfigureAwait(false);
        }
    }

    private async Task AddRowAsync(string path, string rel, Dictionary<string, TreeBlob> before, bool haveTree, string sessionId, bool incomplete)
    {
        // A file the edit tools already changed has its row, and its baseline from the CLI.
        if (_set.TryGet(path, out _)) { return; }

        BaselineRef baseline;
        if (!haveTree)
        {
            baseline = BaselineRef.None;
        }
        else if (before.TryGetValue(rel, out var blob))
        {
            baseline = blob.IsRegularFile ? await CopyBlobAsync(blob, rel, path, sessionId).ConfigureAwait(false) : BaselineRef.None;
        }
        else if (File.Exists(path))
        {
            baseline = BaselineRef.Empty;
        }
        else
        {
            // Not there before and not there now: created and removed within the command.
            return;
        }

        var evaluation = await Task.Run(() => ChangeEvaluator.Evaluate(path, baseline)).ConfigureAwait(false);
        if (evaluation.Unchanged)
        {
            // Touched, not changed: a timestamp, or a file written with what it already held.
            _baselines.Release(baseline);
            return;
        }

        var row = new FileChange
        {
            Path = path,
            Source = ChangeSource.Bash,
            Baseline = baseline,
            Incomplete = incomplete,
            LastChangedUtc = DateTime.UtcNow,
            State = evaluation.State,
            Added = evaluation.Added,
            Deleted = evaluation.Deleted,
            Counts = evaluation.Counts,
            IsBinary = evaluation.IsBinary,
            BaselineMissing = evaluation.BaselineMissing,
            StampLength = evaluation.Length,
            StampWriteUtc = evaluation.WriteUtc,
        };
        if (!_set.Add(row)) { _baselines.Release(baseline); }
    }

    /// <summary>The file as it was before the command, written where a copy of it belongs. None when git
    /// could not produce it, or when it only produced a pointer: a Git LFS file read without the
    /// LFS filter is a few lines naming the real content, and restoring that would destroy the file.</summary>
    private async Task<BaselineRef> CopyBlobAsync(TreeBlob blob, string rel, string path, string sessionId)
    {
        var target = _baselines.TargetFor(sessionId, path);
        if (!await _git.WriteBlobAsync(blob.Oid, rel, target).ConfigureAwait(false)) { return BaselineRef.None; }
        if (LooksLikeLfsPointer(target))
        {
            _baselines.Release(BaselineRef.Own(target));
            return BaselineRef.None;
        }
        return BaselineRef.Own(target);
    }

    private static bool LooksLikeLfsPointer(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[LfsPointer.Length];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length && Encoding.ASCII.GetString(buffer) == LfsPointer;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Take a snapshot now, and remember which events it already contains. Never throws: it
    /// runs in the background, and a failed picture only means the next "before" is an older one.</summary>
    public async Task SnapshotAsync()
    {
        try
        {
            var mark = _watch.Mark();
            var tree = await _git.TakeAsync(_lifetime.Token).ConfigureAwait(false);
            if (tree is not null) { Interlocked.Exchange(ref _snapshotMark, mark); }
        }
        catch (OperationCanceledException)
        {
            // Closed while it ran.
        }
        catch (Exception ex)
        {
            _log.LogException("BashTracker.SnapshotAsync", ex);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _watch.Dispose();
        _git.Dispose();
    }
}

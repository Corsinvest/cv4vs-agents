/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Finds what a shell command changed, and what each file was before it.
/// <para>The CLI's file history never sees a command's edits (<c>sed -i</c>, a script, <c>git checkout</c>),
/// so this keeps its own: a git snapshot of the working tree is "before", another taken when the command
/// ends is "after", and what git says differs between the two is what the command changed. The "before"
/// is taken ahead of the command, because the CLI waits for the PreToolUse hook: one taken later could
/// already hold the command's own writes, and the change would read as no change at all.</para>
/// <para>The file watcher only says whether anything moved, so a command that reads costs no snapshot.
/// It is git that says which files, and how: the same <c>add -A</c> built both pictures, so they agree on
/// the ignore rules and the case of a name, and an event that arrives late or is lost does not matter.</para>
/// <para>Only a git working tree is covered. Elsewhere there is no ignore rule to tell source from build
/// output, so listing "everything a command wrote" would be mostly noise, and nothing to restore from.
/// Files outside the repo, or in a repository nested in it, are left out for the same reason.</para></summary>
internal sealed class BashTracker : IDisposable
{
    // The watcher reports a moment after the command's last write.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    // A command still open after this long was denied, or interrupted, or lost: its place is freed.
    private static readonly TimeSpan CallLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long a command waits for the picture of the tree before it goes ahead without one.</summary>
    internal static readonly TimeSpan DefaultSnapshotWait = TimeSpan.FromSeconds(30);

    // More files than this from one command are not listed one by one: the rest are said to be missing.
    private const int DefaultMaxRows = 500;

    private const string LfsPointer = "version https://git-lfs.github.com/spec/v1";

    /// <summary>What the panel says when git is not installed.</summary>
    internal const string NoGitNote = "Files changed by running a command are not listed: git was not found.";

    /// <summary>What the panel says when the working directory is not in a git repository.</summary>
    internal const string NoRepositoryNote = "Files changed by running a command are not listed: this folder is not in a git repository.";

    /// <summary>What the panel says when the repository is a whole drive or the profile folder.</summary>
    internal const string TooBroadNote = "Files changed by running a command are not listed: the git repository here is your whole profile folder or drive.";

    /// <summary>What the panel says when the folder cannot be watched.</summary>
    internal const string NoWatchNote = "Files changed by running a command are not listed: the folder could not be watched.";

    private sealed class Call
    {
        public long Mark;

        // The picture before the command, or null when there was none to be had in time: the command
        // then runs untracked.
        public GitTree Tree;
        public DateTime AtUtc;
    }

    private readonly GitSnapshot _git;
    private readonly RepoWatch _watch;
    private readonly ChangeSet _set;
    private readonly ChangeBaselines _baselines;
    private readonly OutputWindowLogger _log;
    private readonly int _maxRows;
    private readonly ConcurrentDictionary<string, Call> _calls = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _snapshotMark = -1;

    private BashTracker(GitSnapshot git, RepoWatch watch, ChangeSet set, ChangeBaselines baselines, OutputWindowLogger log, int maxRows)
    {
        _git = git;
        _watch = watch;
        _set = set;
        _baselines = baselines;
        _log = log;
        _maxRows = maxRows;
    }

    /// <summary>Start looking after a working directory. Null when it is not in a git repository, or git
    /// is not there: nothing is then tracked and nothing is listed, and the set says why.</summary>
    public static async Task<BashTracker> StartAsync(string workingDirectory, string snapshotFolder, ChangeSet set,
                                                     ChangeBaselines baselines, OutputWindowLogger log, int? maxRows = null)
    {
        // Looks along PATH on disk: off the thread that asked.
        var exe = await Task.Run(GitExe.Resolve).ConfigureAwait(false);
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

        RepoWatch watch;
        try
        {
            watch = new RepoWatch(snapshot.TopLevel, log);
        }
        catch (Exception ex)
        {
            // A folder the OS will not watch (a share, an unreachable path, too many watchers already).
            log.Warn($"[changes] cannot watch {snapshot.TopLevel}: {ex.Message}");
            snapshot.Dispose();
            set.SetCommandsNote(NoWatchNote);
            return null;
        }

        set.SetCommandsNote(null);
        var tracker = new BashTracker(snapshot, watch, set, baselines, log, maxRows ?? DefaultMaxRows);
        // The first picture, in the background: by the time a command arrives it is usually there, and
        // if it is not the command's own hook waits for it.
        _ = tracker.SnapshotAsync();
        return tracker;
    }

    /// <summary>The root of the working tree being watched.</summary>
    public string TopLevel => _git.TopLevel;

    /// <summary>A command is about to run. Brings the snapshot of the working tree up to date when anything
    /// has changed since it was taken, so it is a true "before"; a quiet working tree costs nothing here.
    /// When the picture is not ready within <paramref name="wait"/>, or <paramref name="late"/> is cancelled
    /// first, the command is not followed: it is about to run whatever happens, and a picture taken while it
    /// runs could hold its own writes. The caller cancels <paramref name="late"/> the moment it lets the
    /// command go.</summary>
    public async Task BeginAsync(string toolUseId, TimeSpan? wait = null, CancellationToken late = default)
    {
        Forget();
        _calls[toolUseId] = await OpenCallAsync(wait ?? DefaultSnapshotWait, late).ConfigureAwait(false);
    }

    /// <summary>The person allowed a command that was waiting for an answer: it starts now, not when the
    /// question was asked, so what they changed meanwhile is theirs and is part of "before". The caller holds
    /// the answer back until this returns, and cancels <paramref name="late"/> before it gives it. A command
    /// that was never begun is left alone.</summary>
    public async Task AllowedAsync(string toolUseId, TimeSpan? wait = null, CancellationToken late = default)
    {
        if (!_calls.TryGetValue(toolUseId, out var asked)) { return; }
        var call = await OpenCallAsync(wait ?? DefaultSnapshotWait, late).ConfigureAwait(false);
        _calls.TryUpdate(toolUseId, call, asked);
    }

    private async Task<Call> OpenCallAsync(TimeSpan wait, CancellationToken late)
    {
        // The mark comes first: whatever happens from here on may already be the command's.
        var mark = _watch.Mark();
        // Already let go: whatever a picture holds now, it was taken with the command running.
        if (late.IsCancellationRequested) { return new Call { Mark = mark, AtUtc = DateTime.UtcNow }; }

        var tree = _git.LatestTree;
        if (tree is null || _watch.ChangedSince(Interlocked.Read(ref _snapshotMark)))
        {
            var taking = SnapshotAsync();
            // A tree that is not fresh is no "before": it would blame the person's own earlier edits on the command.
            tree = await Task.WhenAny(taking, Task.Delay(wait, late)).ConfigureAwait(false) == taking
                ? await taking.ConfigureAwait(false)
                : null;
            if (late.IsCancellationRequested) { tree = null; }
            if (tree is null) { _log.Debug(() => "[changes] no snapshot in time: this command is not followed"); }
        }
        return new Call { Mark = mark, Tree = tree, AtUtc = DateTime.UtcNow };
    }

    /// <summary>A command has finished (or failed). Lists what it changed.</summary>
    public async Task EndAsync(string toolUseId, string sessionId)
    {
        if (!_calls.TryRemove(toolUseId, out var call)) { return; }
        await Task.Delay(SettleTime, _lifetime.Token).ConfigureAwait(false);

        // Nothing moved: a command that only reads costs nothing.
        if (!_watch.ChangedSince(call.Mark) || call.Tree is null) { return; }

        var mark = _watch.Mark();
        var after = await _git.TakeAsync(_lifetime.Token).ConfigureAwait(false);
        if (after is not { Usable: true })
        {
            if (!_lifetime.IsCancellationRequested) { _log.Warn("[changes] no usable snapshot after a command: the files it changed are not listed"); }
            return;
        }
        // Their new content is the next command's "before".
        Interlocked.Exchange(ref _snapshotMark, mark);
        if (after.Oid == call.Tree.Oid) { return; }

        var changes = await _git.DiffAsync(call.Tree.Oid, after.Oid).ConfigureAwait(false);
        if (changes is null) { return; }

        // A nested repository is one entry, its commit: what changes inside it is its own business.
        var rows = changes.Where(c => !c.IsSubmodule).ToList();
        var incomplete = false;
        if (rows.Count > _maxRows)
        {
            rows = rows.Take(_maxRows).ToList();
            incomplete = true;
        }
        foreach (var change in rows)
        {
            if (_lifetime.IsCancellationRequested) { return; }
            await AddRowAsync(change, call.Tree, after, sessionId, incomplete).ConfigureAwait(false);
        }
    }

    private async Task AddRowAsync(TreeChange change, GitTree before, GitTree after, string sessionId, bool incomplete)
    {
        // A file git could not read, in either picture, says nothing about what the command did to it: the
        // tree holds an older version of it, or none. Calling it new or changed is how a file that was
        // there all along gets deleted by an Undo.
        if (before.Skips(change.RelativePath) || after.Skips(change.RelativePath)) { return; }

        var path = _git.Absolute(change.RelativePath);
        // The edit tools already changed this file: it has its row, and its baseline from the CLI.
        if (_set.TryGet(path, out _)) { return; }

        if (change.Status == 'D')
        {
            // Still there: the picture after could not read it. It is not gone.
            if (File.Exists(path)) { return; }
        }
        else if (!change.NewIsFile || !File.Exists(path))
        {
            // A link, or something that was a file and is not any more: nothing to compare or restore.
            return;
        }

        BaselineRef baseline;
        if (change.Status == 'A')
        {
            // Not in the picture before (which could read it), and in the one after: the command made it.
            baseline = BaselineRef.Empty;
        }
        else if (change.OldIsFile)
        {
            baseline = await CopyBlobAsync(change.OldOid, change.RelativePath, path, sessionId).ConfigureAwait(false);
        }
        else
        {
            baseline = BaselineRef.None;
        }

        var evaluation = await Task.Run(() => ChangeEvaluator.Evaluate(path, baseline)).ConfigureAwait(false);
        if (evaluation.Unchanged)
        {
            // Touched, not changed: a mode, or a file written with what it already held.
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
    private async Task<BaselineRef> CopyBlobAsync(string oid, string relativePath, string path, string sessionId)
    {
        var target = _baselines.TargetFor(sessionId, path);
        if (!await _git.WriteBlobAsync(oid, relativePath, target).ConfigureAwait(false)) { return BaselineRef.None; }
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

    /// <summary>Take a snapshot now, and remember which events it already contains. The tree, or null when
    /// there is none to trust (git failed, or skipped files). Never throws: it runs in the background, and a
    /// failed picture only means the next "before" is an older one, or none.</summary>
    public async Task<GitTree> SnapshotAsync()
    {
        try
        {
            var mark = _watch.Mark();
            var tree = await _git.TakeAsync(_lifetime.Token).ConfigureAwait(false);
            if (tree is not { Usable: true }) { return null; }
            Interlocked.Exchange(ref _snapshotMark, mark);
            return tree;
        }
        catch (OperationCanceledException)
        {
            // Closed while it ran.
            return null;
        }
        catch (Exception ex)
        {
            _log.LogException("BashTracker.SnapshotAsync", ex);
            return null;
        }
    }

    /// <summary>Free the places of commands that never reported back.</summary>
    private void Forget()
    {
        var limit = DateTime.UtcNow - CallLifetime;
        foreach (var call in _calls)
        {
            if (call.Value.AtUtc < limit) { _calls.TryRemove(call.Key, out _); }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _watch.Dispose();
        _git.Dispose();
    }
}

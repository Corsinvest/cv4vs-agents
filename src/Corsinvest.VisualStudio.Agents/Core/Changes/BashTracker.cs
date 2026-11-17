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
    // The list holds no more than this in all, so a larger number would only be work thrown away.
    private const int DefaultMaxRows = ChangeSet.MaxFiles;

    // A file the command made has a creation time after the command began. The slack covers the coarse tick
    // a file system stamps a new file with, which can read a little before a clock taken just ahead of it.
    // No more: a file the person or the editor made in the moments before is not the command's, and Undo
    // deletes what it takes for new. A file system that keeps coarser time than this errs the safe way,
    // taking a new file for an old one, which is listed without a copy.
    private static readonly TimeSpan CreationSlack = TimeSpan.FromMilliseconds(250);

    // An event can arrive late on a busy machine. After the settle time, nothing seen is looked at once more
    // before the command is taken to have changed nothing: the cost is a second of waiting nobody is
    // waiting for, and what it buys is a change that is not absorbed into the next command's "before".
    private static readonly TimeSpan LateEventsWait = TimeSpan.FromSeconds(1);

    // A file larger than this is not copied for an Undo: a command that rewrites a dataset or an installer
    // should not fill the disk with the version before it. Listed with no copy, so it cannot be undone.
    internal const long DefaultMaxBaselineBytes = 100L * 1024 * 1024;

    private const string LfsPointer = "version https://git-lfs.github.com/spec/v1";

    /// <summary>What the panel says when git is not installed.</summary>
    internal const string NoGitNote = "Files changed by running a command are not listed: git was not found.";

    /// <summary>What the panel says when the working directory is not in a git repository.</summary>
    internal const string NoRepositoryNote = "Files changed by running a command are not listed: this folder is not in a git repository.";

    /// <summary>What the panel says when the repository is a whole drive or the profile folder.</summary>
    internal const string TooBroadNote = "Files changed by running a command are not listed: the git repository here is your whole profile folder or drive.";

    /// <summary>What the panel says when git refuses the repository because it belongs to another user.</summary>
    internal const string NotYoursNote = "Files changed by running a command are not listed: git refuses this repository because it belongs to another user (see git config --global --add safe.directory).";

    /// <summary>What the panel says when git did not answer in time.</summary>
    internal const string NoAnswerNote = "Files changed by running a command are not listed: git did not answer for this folder.";

    /// <summary>What the panel says when the folder cannot be watched.</summary>
    internal const string NoWatchNote = "Files changed by running a command are not listed: the folder could not be watched.";

    private sealed class Call
    {
        public long Mark;

        // The picture before the command, or null when there was none to be had in time: the command
        // then runs untracked.
        public GitTree Tree;
        public DateTime AtUtc;

        // The conversation it began in: a command that ends after the person started another is not
        // part of that one's list.
        public string SessionId;
    }

    private readonly GitSnapshot _git;
    private readonly RepoWatch _watch;
    private readonly ChangeSet _set;
    private readonly ChangeBaselines _baselines;
    private readonly OutputWindowLogger _log;
    private readonly int _maxRows;
    private readonly Func<string, Task> _refreshRow;
    private readonly long _maxBaselineBytes;
    private readonly ConcurrentDictionary<string, Call> _calls = new();
    private readonly CancellationTokenSource _lifetime = new();
    private long _snapshotMark = -1;

    private BashTracker(GitSnapshot git, RepoWatch watch, ChangeSet set, ChangeBaselines baselines, OutputWindowLogger log,
                        int maxRows, Func<string, Task> refreshRow, long maxBaselineBytes)
    {
        _maxBaselineBytes = maxBaselineBytes;
        _git = git;
        _watch = watch;
        _set = set;
        _baselines = baselines;
        _log = log;
        _maxRows = maxRows;
        _refreshRow = refreshRow;
    }

    /// <summary>Start looking after a working directory. Null when it is not in a git repository, or git
    /// is not there: nothing is then tracked and nothing is listed, and the set says why.
    /// <paramref name="refreshRow"/> measures a file that already has a row again: a command that changes it
    /// once more, or puts it back, must show.</summary>
    public static async Task<BashTracker> StartAsync(string workingDirectory, string snapshotFolder, ChangeSet set,
                                                     ChangeBaselines baselines, OutputWindowLogger log, int? maxRows = null,
                                                     Func<string, Task> refreshRow = null, long? maxBaselineBytes = null)
    {
        // Looks along PATH on disk: off the thread that asked.
        var exe = await Task.Run(GitExe.Resolve).ConfigureAwait(false);
        if (exe is null)
        {
            log.Info("[changes] no git found: files changed by commands are not tracked");
            set.SetCommandsNote(NoGitNote);
            return null;
        }

        string whyNot = null;
        var snapshot = await GitSnapshot.OpenAsync(new GitRunner(exe, log), workingDirectory, snapshotFolder, log, said => whyNot = said).ConfigureAwait(false);
        if (snapshot is null)
        {
            log.Info($"[changes] {workingDirectory}: git would not open it ({(string.IsNullOrWhiteSpace(whyNot) ? "no answer" : whyNot.Trim())}): files changed by commands are not tracked");
            set.SetCommandsNote(FailureNote(whyNot));
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
        // No picture yet: reading every file of a working tree is not for a chat that has done nothing.
        // See WarmUpAsync, and BeginAsync for the command that arrives first.
        return new BashTracker(snapshot, watch, set, baselines, log, maxRows ?? DefaultMaxRows, refreshRow, maxBaselineBytes ?? DefaultMaxBaselineBytes);
    }

    /// <summary>What the panel says for what git answered when it would not open the folder: "not a
    /// repository" is the common case and the plain one, but a repository git refuses (it belongs to
    /// another user) or does not answer for would otherwise be reported as no repository at all.</summary>
    internal static string FailureNote(string stderr)
    {
        if (stderr is null || stderr.Trim().Length == 0) { return NoAnswerNote; }
        if (stderr.IndexOf("dubious ownership", StringComparison.OrdinalIgnoreCase) >= 0) { return NotYoursNote; }
        if (stderr.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0) { return NoRepositoryNote; }

        var said = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (said.Length > 120) { said = said.Substring(0, 120) + "..."; }
        return "Files changed by running a command are not listed: git said " + said;
    }

    /// <summary>The root of the working tree being watched.</summary>
    public string TopLevel => _git.TopLevel;

    /// <summary>Take the first picture of the tree, if there is none yet. Called when Claude starts working,
    /// so it is there by the time the first command arrives; a command that arrives before it is done waits
    /// for it, as it would have anyway.</summary>
    public async Task WarmUpAsync()
    {
        if (_git.LatestTree is null) { await SnapshotAsync().ConfigureAwait(false); }
    }

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
        if (late.IsCancellationRequested) { return new Call { Mark = mark, AtUtc = DateTime.UtcNow, SessionId = _set.SessionId }; }

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
        return new Call { Mark = mark, Tree = tree, AtUtc = DateTime.UtcNow, SessionId = _set.SessionId };
    }

    /// <summary>A command has finished (or failed). Lists what it changed.</summary>
    public async Task EndAsync(string toolUseId, string sessionId)
    {
        if (!_calls.TryRemove(toolUseId, out var call)) { return; }
        await Task.Delay(SettleTime, _lifetime.Token).ConfigureAwait(false);

        if (call.Tree is null) { return; }
        if (!_watch.ChangedSince(call.Mark))
        {
            await Task.Delay(LateEventsWait, _lifetime.Token).ConfigureAwait(false);
            // Still nothing: a command that only reads costs nothing.
            if (!_watch.ChangedSince(call.Mark)) { return; }
        }

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
        // The person started another conversation while this ran: what it changed belongs to the old one.
        if (!string.Equals(call.SessionId, _set.SessionId, StringComparison.Ordinal)) { return; }

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
            if (_set.IsFull && !_set.TryGet(_git.Absolute(change.RelativePath), out _))
            {
                // No room for a file that is not listed: it would be read, compared and thrown away. One
                // that is listed costs no room, and is still measured again.
                _set.NoteOverflow();
                continue;
            }
            await AddRowAsync(change, call.Tree, after, call.AtUtc, sessionId, incomplete).ConfigureAwait(false);
        }
    }

    private async Task AddRowAsync(TreeChange change, GitTree before, GitTree after, DateTime beganUtc, string sessionId, bool incomplete)
    {
        // A file git could not read, in either picture, says nothing about what the command did to it: the
        // tree holds an older version of it, or none. Calling it new or changed is how a file that was
        // there all along gets deleted by an Undo.
        if (before.Skips(change.RelativePath) || after.Skips(change.RelativePath)) { return; }

        var path = _git.Absolute(change.RelativePath);
        // Already listed (the edit tools changed it, or an earlier command did): the row keeps its baseline,
        // and its numbers move. A file put back to how it was leaves the list.
        if (_set.TryGet(path, out _))
        {
            if (_refreshRow is not null) { await _refreshRow(path).ConfigureAwait(false); }
            return;
        }

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
            // Absent from the picture before is not proof the command made the file: one the repository
            // ignored until the command changed the rules (an edit of .gitignore, a checkout) was there all
            // along, and an Undo that deletes a file that is new deletes this one too. The command made it
            // if its creation time is after the command began. Anything else is left without a copy, so
            // it is listed but not undone.
            baseline = File.GetCreationTimeUtc(path) >= beganUtc - CreationSlack ? BaselineRef.Empty : BaselineRef.None;
        }
        else if (change.OldIsFile && (change.Status == 'D' || new FileInfo(path).Length <= _maxBaselineBytes))
        {
            baseline = await CopyBlobAsync(change.OldOid, change.RelativePath, path, sessionId).ConfigureAwait(false);
        }
        else
        {
            baseline = BaselineRef.None;
        }

        var evaluation = await ChangeEvaluator.EvaluateAsync(path, baseline).ConfigureAwait(false);
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
        // Stored or not, the copy is the row's only if the row is. One that got in first (the edit tools, or
        // another command ending) keeps its own baseline, and this copy is surplus.
        if (_set.TryAdd(row) != AddResult.Added) { _baselines.Release(baseline); }
    }

    /// <summary>The file as it was before the command, written where a copy of it belongs. None when git
    /// could not produce it, or when it only produced a pointer: a Git LFS file read without the
    /// LFS filter is a few lines naming the real content, and restoring that would destroy the file.</summary>
    private async Task<BaselineRef> CopyBlobAsync(string oid, string relativePath, string path, string sessionId)
    {
        var target = _baselines.TargetFor(sessionId, path);
        if (!await _git.WriteBlobAsync(oid, relativePath, target).ConfigureAwait(false)) { return BaselineRef.None; }
        if (await LooksLikeAnLfsPointerAsync(target).ConfigureAwait(false))
        {
            _baselines.Release(BaselineRef.Own(target));
            return BaselineRef.None;
        }
        return BaselineRef.Own(target);
    }

    private async Task<bool> LooksLikeAnLfsPointerAsync(string file)
    {
        try
        {
            var head = await AsyncFile.ReadHeadAsync(file, LfsPointer.Length).ConfigureAwait(false);
            return head.Length == LfsPointer.Length && Encoding.ASCII.GetString(head) == LfsPointer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not read, so not known not to be a pointer, and a pointer restored over the real file would
            // destroy it: taken for one, which leaves the file listed without a copy.
            _log.Warn($"[changes] could not read {file} to check it for a Git LFS pointer: {ex.Message}");
            return true;
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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Sessions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>How a pane's <see cref="SessionChanges"/> is wired to the rest of the extension, so the
/// logic can run under a test without Visual Studio.</summary>
internal sealed class SessionChangesOptions
{
    /// <summary>Whether the CLI keeps file history for this session. Without it there are no
    /// baselines to compare against, and tracking is switched off rather than half-working.</summary>
    public Func<bool> FileCheckpoints { get; set; } = () => true;

    /// <summary>Whether the files a shell command changes are followed (the setting). Read each time the
    /// panel is built, so the note it shows follows the setting.</summary>
    public Func<bool> TrackCommands { get; set; } = () => true;

    /// <summary>Runs before a file is copied, to get unsaved editor changes onto disk: the copy must
    /// be of what Claude is about to change, not of a stale file. Null = nothing to do.</summary>
    public Func<string, Task> PrepareFile { get; set; }

    /// <summary>The waits before each look for the CLI's delta after a hook. The record is written
    /// about 100 ms after the hook (measured), so most edits are found on the second try.</summary>
    public int[] PollMilliseconds { get; set; } = { 0, 120, 250, 500, 1000, 2000, 4000 };

    /// <summary>Where this extension's own copies go. Null = the running extension's folder.</summary>
    public ChangeBaselines Baselines { get; set; }

    /// <summary>The editor Keep, Undo and Open act through. Null = Visual Studio.</summary>
    public IChangeEditor Editor { get; set; }

    /// <summary>The number this pane's tab shows ("Chat 5"): the one thing that has to be named to another pane is the file
    /// two panes list. Null = none (a test).</summary>
    public Func<int> PaneNumber { get; set; }

    /// <summary>What was kept in this session, remembered across a restart. Null = a ledger in the running extension's folder.</summary>
    public KeptLedger Kept { get; set; }

    /// <summary>Where the git snapshots of the working tree go. Null = the running extension's folder.</summary>
    public string SnapshotsRoot { get; set; }

    /// <summary>How many files one shell command may add to the list before the rest are left out and
    /// the rows say so. Null = the default; a test sets it low to reach the cap without writing hundreds
    /// of files.</summary>
    public int? MaxRowsPerCommand { get; set; }

    /// <summary>The largest file a command may change and still get a copy of how it was, for Undo. Null =
    /// the default; a test sets it low.</summary>
    public long? MaxBaselineBytes { get; set; }
}

/// <summary>Everything one chat pane knows about the files Claude changed: it feeds the
/// <see cref="ChangeSet"/> from the hook callbacks and from the transcript, and keeps it truthful
/// against the disk.
/// <para>Where the "before" content comes from, in order: a copy taken at the PreToolUse hook (a
/// sub-agent's edit, or the first edit after a Keep or Undo, neither of which the CLI backs up); the
/// CLI's own backup, found in the transcript; nothing, in which case the file is listed but cannot be
/// compared or restored.</para></summary>
internal sealed partial class SessionChanges : IDisposable
{
    // A copy taken for a tool call that never reported back (refused, failed, interrupted) is dropped after this.
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    // Orphaned session folders older than this go, once per process.
    private static readonly TimeSpan SweepAge = TimeSpan.FromDays(14);
    private static int _swept;

    // What was kept in a chat that nobody has resumed or kept anything in for this long goes, once per process.
    private static readonly TimeSpan KeptAge = TimeSpan.FromDays(30);

    private readonly KeptLedger _kept;
    private readonly ClaudePaths _paths;
    private readonly string _workingDirectory;
    private readonly SessionChangesOptions _options;
    private readonly OutputWindowLogger _log;
    private readonly ChangeBaselines _baselines;
    private readonly ChangeSet _set = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();
    private int _generation;
    private bool _disposed;
    private Task<BashTracker> _bash;

    public SessionChanges(ClaudePaths paths, string workingDirectory, SessionChangesOptions options, OutputWindowLogger log)
    {
        _paths = paths;
        _workingDirectory = workingDirectory;
        _options = options ?? new SessionChangesOptions();
        _log = log ?? OutputWindowLogger.Global;
        _baselines = _options.Baselines ?? ChangeBaselines.Default;
        _kept = _options.Kept ?? new KeptLedger(AppPaths.ChangesFolder, _log);
        Actions = new ChangeActions(_set, _baselines, _options.Editor ?? IdeChangeEditor.Instance, _log,
                                    path => RefreshRowAsync(path, null), path => _kept.KeepAsync(path));
        if (_baselines == ChangeBaselines.Default && Interlocked.Exchange(ref _swept, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                _baselines.Sweep(SweepAge);
                new ChangeBaselines(AppPaths.SnapshotsFolder, _log).Sweep(SweepAge);
                KeptLedger.Sweep(AppPaths.ChangesFolder, KeptAge, _log);
            });
        }
    }

    public ChangeSet Set => _set;

    /// <summary>The number this pane's tab shows, or 0 when it has none.</summary>
    public int PaneNumber => _options.PaneNumber?.Invoke() ?? 0;

    /// <summary>Where this extension's own copies go.</summary>
    public ChangeBaselines Baselines => _baselines;

    /// <summary>The session the list belongs to, or null before one is known.</summary>
    public string SessionId => _set.SessionId;

    /// <summary>Measure a listed file again, for whoever has just written it.</summary>
    public Task RemeasureAsync(string path) => RefreshRowAsync(path, null);

    /// <summary>The row's baseline becomes <paramref name="bytes"/>, a copy of this extension's own: what a hunk kept in
    /// the editor turns the baseline into, so that it stays kept for the counts, for Open and for Undo. The copy the row had
    /// goes, if it was ours. Only the row <paramref name="serial"/> names: a row that has been replaced since (kept, then
    /// changed again) is not the one the editor was showing, and its fresh baseline is not to be overwritten. Calls for one file
    /// go one at a time. Returns the path of the new copy, or null when the row is gone, is another, or the copy could not be
    /// written (the row then keeps the baseline it had).</summary>
    public Task<string> ReplaceBaselineAsync(string path, long serial, byte[] bytes) => Task.Run(async () =>
    {
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            if (_disposed || !_set.TryGet(path, out var row) || row.Serial != serial) { return null; }
            string target = null;
            try
            {
                target = _baselines.TargetFor(_set.SessionId, path);
                await AsyncFile.WriteAllBytesAsync(target, bytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"[changes] could not write the new baseline of {path}: {ex.Message}");
                // Half a copy is of no use to anybody, and nothing else holds it.
                if (target is not null) { _baselines.Release(BaselineRef.Own(target)); }
                return null;
            }

            var fresh = BaselineRef.Own(target);
            var replaced = BaselineRef.None;
            var applied = false;
            _set.Update(path, r =>
            {
                if (r.Serial != serial) { return; }
                replaced = r.Baseline;
                r.Baseline = fresh;
                r.BaselineMissing = false;
                applied = true;
            });
            if (!applied)
            {
                // The row went, or was replaced, while the copy was being written: nothing holds it.
                _baselines.Release(fresh);
                return null;
            }
            _baselines.Release(replaced);
            await RefreshRowAsync(path, null).ConfigureAwait(false);
            return target;
        }
        finally
        {
            gate.Release();
        }
    });

    /// <summary>What the panel says about shell commands: why they are not followed, or null when they
    /// are (or it is not yet known, before the first look at the folder).</summary>
    public string CommandsNote => TracksCommands ? _set.CommandsNote : CommandsOffNote;

    /// <summary>Whether the files a shell command changes are followed (the setting).</summary>
    public bool TracksCommands => _options.TrackCommands();

    /// <summary>Whether anything would come of following them now: the setting is on, and so is the list
    /// itself, which needs the CLI's file checkpoints. Without them nothing is listed, and a picture taken
    /// or an answer held back would be for nothing.</summary>
    public bool FollowsCommands => TracksCommands && _options.FileCheckpoints();

    internal const string CommandsOffNote = "Files changed by running a command are not listed: that is turned off in Options.";

    /// <summary>What the Open, Keep and Undo buttons do.</summary>
    public ChangeActions Actions { get; }

    private SessionManager Sessions => new(_paths, _workingDirectory, _log);

    /// <summary>A different conversation is starting in this pane (New, History pick, /clear): the old
    /// list is not this one's. <paramref name="resume"/> rebuilds it from the CLI's history.</summary>
    public void BeginSession(string sessionId, bool resume)
    {
        ReleaseAll(_set.Reset(sessionId));
        _kept.Open(sessionId);
        var generation = Interlocked.Increment(ref _generation);
        if (resume && !string.IsNullOrEmpty(sessionId)) { _ = RebuildAsync(sessionId, generation); }
    }

    /// <summary>Follow the session a hook or the CLI names. A different id than the one the list holds
    /// means a fork or a reset happened without us being told.</summary>
    public void EnsureSession(string sessionId)
    {
        if (_set.EnsureSession(sessionId, out var released))
        {
            ReleaseAll(released);
            Interlocked.Increment(ref _generation);
        }
        // Idempotent: a hook names the session each time, and only the first one after a change of session does anything.
        _kept.Open(_set.SessionId);
    }

    /// <summary>Start following what shell commands change: find the git repository of the working
    /// directory and watch it. In the background, and once; nothing happens outside a git working tree.
    /// Called when Claude is about to work (<see cref="WarmUp"/>) or the first command arrives, and not
    /// before: a chat that never runs a command, or is only restored with the solution, runs no git, watches
    /// no folder and reads no disk.</summary>
    public void StartTracking()
    {
        if (_disposed || _bash is not null) { return; }
        var folder = Path.Combine(_options.SnapshotsRoot ?? AppPaths.SnapshotsFolder, Guid.NewGuid().ToString("N"));
        _bash = BashTracker.StartAsync(_workingDirectory, folder, _set, _baselines, _log, _options.MaxRowsPerCommand,
                                       path => RefreshRowAsync(path, null), _options.MaxBaselineBytes);
    }

    /// <summary>Claude is about to work: take the first picture of the working tree now, in the background,
    /// so that it is there by the time the first command arrives and that command does not wait for it.
    /// Later pictures are taken when a command finds the tree has changed.</summary>
    public void WarmUp()
    {
        if (_disposed || !FollowsCommands) { return; }
        StartTracking();
        _ = WarmUpAsync();
    }

    private async Task WarmUpAsync()
    {
        try
        {
            var tracker = await Tracker().ConfigureAwait(false);
            if (tracker is not null) { await tracker.WarmUpAsync().ConfigureAwait(false); }
        }
        catch (OperationCanceledException)
        {
            // The pane closed.
        }
        catch (Exception ex)
        {
            _log.LogException("SessionChanges.WarmUpAsync", ex);
        }
    }

    /// <summary>A shell command is about to run. Brings the snapshot of the working tree up to date when
    /// something has changed since it was taken, so there is a true "before" to compare with. Bounded by
    /// the caller: the CLI waits for this answer before it runs the command, and <paramref name="late"/> is
    /// cancelled the moment it is given, since a picture that finishes after that holds the command's own writes.</summary>
    public async Task OnBashBeginAsync(HookBash call, CancellationToken late = default)
    {
        try
        {
            if (!Active(call.SessionId)) { return; }
            var tracker = await Tracker().ConfigureAwait(false);
            if (tracker is not null) { await tracker.BeginAsync(call.ToolUseId, late: late).ConfigureAwait(false); }
        }
        catch (OperationCanceledException)
        {
            // The pane closed.
        }
        catch (Exception ex)
        {
            _log.LogException("SessionChanges.OnBashBeginAsync", ex);
        }
    }

    /// <summary>How long the answer to a permission question is held for the snapshot, at most.</summary>
    public static readonly TimeSpan AllowedWait = TimeSpan.FromSeconds(10);

    /// <summary>The person allowed a shell command that was waiting for an answer. The hook before it ran
    /// when the question was asked; what the person did in the editor since is not the command's, so the
    /// picture of the working tree is brought up to date now. Bounded by the caller, which holds the answer
    /// back (the CLI runs the command as soon as it has it) and cancels <paramref name="late"/> before it
    /// gives it.</summary>
    public async Task OnBashAllowedAsync(string toolUseId, CancellationToken late = default)
    {
        try
        {
            var tracker = await Tracker().ConfigureAwait(false);
            if (tracker is not null) { await tracker.AllowedAsync(toolUseId, AllowedWait, late).ConfigureAwait(false); }
        }
        catch (OperationCanceledException)
        {
            // The pane closed.
        }
        catch (Exception ex)
        {
            _log.LogException("SessionChanges.OnBashAllowedAsync", ex);
        }
    }

    /// <summary>A shell command has finished, or failed: list the files it changed. Not waited for.</summary>
    public async Task OnBashEndAsync(HookBash call)
    {
        try
        {
            if (!Active(call.SessionId)) { return; }
            var tracker = await Tracker().ConfigureAwait(false);
            if (tracker is not null) { await tracker.EndAsync(call.ToolUseId, _set.SessionId).ConfigureAwait(false); }
        }
        catch (OperationCanceledException)
        {
            // The pane closed.
        }
        catch (Exception ex)
        {
            _log.LogException("SessionChanges.OnBashEndAsync", ex);
        }
    }

    private async Task<BashTracker> Tracker()
    {
        var started = _bash;
        return started is null ? null : await started.ConfigureAwait(false);
    }

    /// <summary>The hook that runs before an edit. Takes the copy the CLI will not: only for a
    /// sub-agent, or for a file the user just kept or undid. Bounded by the caller, because the CLI
    /// waits for this answer before it runs the tool, and <paramref name="late"/> is cancelled the
    /// moment it is given: a copy finished after that was taken with the edit under way, and would
    /// hold it.</summary>
    public async Task OnPreEditAsync(HookEdit edit, CancellationToken late = default)
    {
        try
        {
            // No tool call to pair the copy with: the hook after the edit could never claim it.
            if (!Active(edit) || string.IsNullOrEmpty(edit.ToolUseId)) { return; }
            var path = ChangePaths.Normalize(edit.FilePath, string.IsNullOrEmpty(edit.Cwd) ? _workingDirectory : edit.Cwd);
            if (path is null || _set.TryGet(path, out _)) { return; }
            if (!edit.IsSubAgent && !_set.WantsCapture(path)) { return; }

            ReleaseAll(_set.PrunePending(PendingLifetime));
            if (_options.PrepareFile is not null) { await _options.PrepareFile(path).ConfigureAwait(false); }
            if (late.IsCancellationRequested) { return; }
            var sessionId = _set.SessionId;
            var baseline = await _baselines.CaptureAsync(sessionId, path).ConfigureAwait(false);
            if (late.IsCancellationRequested)
            {
                _baselines.Release(baseline);
                return;
            }
            _set.StorePending(edit.ToolUseId, path, baseline);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("SessionChanges.OnPreEditAsync", ex);
        }
    }

    /// <summary>The hook that runs after an edit. Never throws, and does not hold the CLI up: the
    /// caller does not wait for it, so the list simply gains its row a moment later.</summary>
    public async Task OnPostEditAsync(HookEdit edit)
    {
        try
        {
            if (!Active(edit)) { return; }
            var path = ChangePaths.Normalize(edit.FilePath, string.IsNullOrEmpty(edit.Cwd) ? _workingDirectory : edit.Cwd);
            if (path is null) { return; }

            // One file at a time: two quick edits of a new file must not both go looking for its
            // baseline and both add a row.
            var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { await RecordAsync(edit, path).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException)
        {
            // The pane closed while this waited for the CLI to write its record.
        }
        catch (Exception ex)
        {
            _log.LogException("SessionChanges.OnPostEditAsync", ex);
        }
    }

    private async Task RecordAsync(HookEdit edit, string path)
    {
        var generation = Volatile.Read(ref _generation);
        if (_set.TryGet(path, out _))
        {
            // Already listed: the baseline stays the first one, only the numbers move.
            if (_set.TakePending(edit.ToolUseId, path, out var spare)) { _baselines.Release(spare); }
            await RefreshRowAsync(path, edit.ToolUseId).ConfigureAwait(false);
            return;
        }

        Found found;
        if (_set.TakePending(edit.ToolUseId, path, out var captured))
        {
            found = new Found(captured, false);
        }
        else if (_set.WantsCapture(path))
        {
            // Kept or undone earlier, and the copy for this edit is missing (the hook was late, or named no
            // tool call). The CLI's own copy is from before the whole session: comparing with it would offer
            // to undo what the person already accepted. Better listed with no copy than with that one.
            _log.Warn($"[changes] no copy was taken before this edit of {path}, which was kept or undone before: it is listed without one");
            found = new Found(BaselineRef.None, false);
        }
        else
        {
            found = await FindCliBaselineAsync(edit, path).ConfigureAwait(false);
        }

        // A new session began while this waited: the result belongs to the old one.
        if (generation != Volatile.Read(ref _generation))
        {
            _baselines.Release(found.Baseline);
            return;
        }
        await AddRowAsync(path, found, edit.ToolUseId).ConfigureAwait(false);
    }

    private readonly struct Found
    {
        public Found(BaselineRef baseline, bool missing)
        {
            Baseline = baseline;
            Missing = missing;
        }

        public BaselineRef Baseline { get; }

        /// <summary>The CLI named a backup that is gone.</summary>
        public bool Missing { get; }
    }

    private async Task<Found> FindCliBaselineAsync(HookEdit edit, string path)
    {
        var sessions = Sessions;
        var sessionId = string.IsNullOrEmpty(edit.SessionId) ? _set.SessionId : edit.SessionId;
        // A sub-agent's edit has no record to wait for (measured: the CLI writes none), so it gets
        // one look, for a file the main agent tracked earlier, and no more.
        foreach (var wait in edit.IsSubAgent ? new[] { 0 } : _options.PollMilliseconds)
        {
            if (wait > 0) { await Task.Delay(wait, _lifetime.Token).ConfigureAwait(false); }
            var entry = await sessions.FindRecentDeltaAsync(sessionId, edit.TranscriptPath, path).ConfigureAwait(false);
            if (entry is not null) { return ToFound(sessions, sessionId, entry); }
        }

        // No fresh record. The CLI writes one only the first time it tracks a file, so this file was
        // tracked in an earlier turn that this pane did not see (or the record is lost): the
        // history holds it, and the earliest record is the content from before the session's first edit.
        var history = await sessions.ReadFileHistoryAsync(sessionId).ConfigureAwait(false);
        if (history.TryGetValue(path, out var track)) { return ToFound(sessions, sessionId, track.Earliest); }

        _log.Warn($"[changes] no baseline for {path}: the CLI kept no copy of it, so it is listed without one");
        return new Found(BaselineRef.None, false);
    }

    private static Found ToFound(SessionManager sessions, string sessionId, SessionManager.FileHistoryEntry entry)
    {
        if (entry.BackupFileName is null) { return new Found(BaselineRef.Empty, false); }
        var backup = sessions.BackupPath(sessionId, entry);
        return backup is not null && System.IO.File.Exists(backup)
            ? new Found(BaselineRef.Cli(backup), false)
            : new Found(BaselineRef.None, true);
    }

    private async Task AddRowAsync(string path, Found found, string toolUseId)
    {
        var evaluation = await ChangeEvaluator.EvaluateAsync(path, found.Baseline).ConfigureAwait(false);
        if (evaluation.Unchanged)
        {
            // Edited back to what it was, or created and removed again: nothing to review. The CLI
            // has no backup to offer for the next edit of this file, so that one gets a copy.
            _baselines.Release(found.Baseline);
            _set.SetCaptureNext(path);
            return;
        }

        var row = NewRow(path, found, evaluation, toolUseId);
        var alreadyFull = _set.Overflow;
        switch (_set.TryAdd(row))
        {
            case AddResult.Added:
                _set.ConsumeCaptureNext(path);
                return;
            case AddResult.AlreadyListed:
                _set.ConsumeCaptureNext(path);
                // Not stored, so nothing holds the copy that was taken for it: a row that was there first keeps its own.
                _baselines.Release(found.Baseline);
                return;
            case AddResult.Full:
                _baselines.Release(found.Baseline);
                if (!alreadyFull) { _log.Warn($"[changes] more than {ChangeSet.MaxFiles} files changed: the rest are not listed"); }
                return;
        }
    }

    private static FileChange NewRow(string path, Found found, ChangeEvaluation evaluation, string toolUseId)
    {
        var row = new FileChange
        {
            Path = path,
            Source = ChangeSource.Edit,
            Baseline = found.Baseline,
            FirstToolUseId = toolUseId,
            LastToolUseId = toolUseId,
            LastChangedUtc = DateTime.UtcNow,
        };
        Apply(row, evaluation, found.Missing);
        return row;
    }

    private static void Apply(FileChange row, ChangeEvaluation evaluation, bool baselineMissing)
    {
        row.State = evaluation.State;
        row.Added = evaluation.Added;
        row.Deleted = evaluation.Deleted;
        row.Counts = evaluation.Counts;
        row.IsBinary = evaluation.IsBinary;
        row.BaselineMissing = baselineMissing || evaluation.BaselineMissing;
        row.StampLength = evaluation.Length;
        row.StampWriteUtc = evaluation.WriteUtc;
    }

    /// <summary>Measure a listed file again. A file that has become identical to its baseline leaves
    /// the list: whatever undid the change, there is nothing left to review.</summary>
    private async Task RefreshRowAsync(string path, string toolUseId)
    {
        // A hunk kept in the editor replaces the row's baseline while this measures against the old one, and deletes it: that
        // measurement is of a copy that is gone, and says nothing about the row. It is thrown away and made again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!_set.TryGet(path, out var row)) { return; }
            var measured = row.Baseline;
            var evaluation = await ChangeEvaluator.EvaluateAsync(path, measured).ConfigureAwait(false);
            if (evaluation.Unchanged)
            {
                var removed = _set.RemoveIf(path, r => SameBaseline(r.Baseline, measured));
                if (removed is null) { continue; }
                _baselines.Release(removed.Baseline);
                _set.SetCaptureNext(path);
                // A baseline that is this extension's own is one a hunk kept in the editor made: the file that has become equal to it
                // was kept, change by change, and never passed Keep. It is remembered like one, or the next run, which compares the
                // file with the CLI's copy, would list it again with the changes the person had accepted.
                if (removed.Baseline.Owned) { await _kept.KeepAsync(path).ConfigureAwait(false); }
                return;
            }

            var stale = false;
            var updated = _set.Update(path, r =>
            {
                if (!SameBaseline(r.Baseline, measured)) { stale = true; return; }
                Apply(r, evaluation, r.BaselineMissing);
                if (toolUseId is null) { return; }
                r.LastToolUseId = toolUseId;
                r.LastChangedUtc = DateTime.UtcNow;
            });
            if (!updated || !stale) { return; }
        }
    }

    private static bool SameBaseline(BaselineRef a, BaselineRef b)
        => a.IsEmpty == b.IsEmpty && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Bring every listed file up to date with the disk, for files the user edited, reverted
    /// or deleted since. A file whose size and write time have not moved is not measured again, so
    /// calling this when the panel opens costs one stat per row.</summary>
    public Task RefreshAsync() => Task.Run(async () =>
    {
        foreach (var row in _set.Snapshot())
        {
            if (_disposed) { return; }
            var info = new System.IO.FileInfo(row.Path);
            var length = info.Exists ? info.Length : 0;
            var written = info.Exists ? info.LastWriteTimeUtc : default;
            if (length == row.StampLength && written == row.StampWriteUtc) { continue; }
            await RefreshRowAsync(row.Path, null).ConfigureAwait(false);
        }
    });

    /// <summary>The list for a session that is being resumed: every file the CLI's history shows
    /// Claude touched, measured against its earliest backup. Files that are back to that content are
    /// left out, so only what still differs is listed.</summary>
    private async Task RebuildAsync(string sessionId, int generation)
    {
        try
        {
            if (!_options.FileCheckpoints())
            {
                _set.SetDisabled(NoCheckpoints);
                return;
            }
            _set.SetDisabled(null);

            var sessions = Sessions;
            var keptPaths = new List<string>();
            var rows = new List<FileChange>();
            var history = await sessions.ReadFileHistoryAsync(sessionId).ConfigureAwait(false);
            foreach (var kv in history.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) { break; }
                // Accepted before the restart, and not changed since: not a change to review.
                if (await _kept.IsStillKeptAsync(kv.Key).ConfigureAwait(false))
                {
                    keptPaths.Add(kv.Key);
                    continue;
                }
                var baseline = ToFound(sessions, sessionId, kv.Value.Earliest);
                var evaluation = await ChangeEvaluator.EvaluateAsync(kv.Key, baseline.Baseline).ConfigureAwait(false);
                if (evaluation.Unchanged) { continue; }
                rows.Add(NewRow(kv.Key, baseline, evaluation, null));
                if (rows.Count > ChangeSet.MaxFiles) { break; }
            }

            // The next edit of a file kept before the restart is compared with a copy taken just before it, as after a Keep in this
            // run: the CLI's own copy is from before the whole session, and Undo against it would take back what was accepted.
            foreach (var path in keptPaths)
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) { return; }
                _set.SetCaptureNext(path);
            }
            foreach (var row in rows)
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) { return; }
                if (!_set.Add(row)) { break; }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogException("SessionChanges.RebuildAsync", ex);
        }
    }

    /// <summary>Why nothing is listed when file history is off.</summary>
    public const string NoCheckpoints = "Keep file checkpoints is off, so there is nothing to compare Claude's edits with.";

    private bool Active(HookEdit edit) => Active(edit.SessionId);

    private bool Active(string sessionId)
    {
        if (_disposed) { return false; }
        if (!_options.FileCheckpoints())
        {
            _set.SetDisabled(NoCheckpoints);
            return false;
        }
        _set.SetDisabled(null);
        EnsureSession(sessionId);
        return true;
    }

    // Deleting has no async form. Only the copies this extension took are deleted (a sub-agent's, a file after Keep), a few
    // small files, so a new chat or a closing pane does not wait for them in any way that matters.
    private void ReleaseAll(IEnumerable<BaselineRef> baselines)
    {
        if (baselines is null) { return; }
        foreach (var baseline in baselines) { _baselines.Release(baseline); }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        // Cancelled, not disposed: a hook still waiting on its first look at the transcript asks for
        // the token, and a disposed source throws from it.
        _lifetime.Cancel();
        ReleaseAll(_set.Reset(null));
        _kept.Dispose();

        // Its folder of git snapshots goes with it, once it has finished starting (or failing to).
        var bash = _bash;
        _bash = null;
        if (bash is not null)
        {
            _ = bash.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) { t.Result?.Dispose(); } },
                                  TaskScheduler.Default);
        }
    }
}

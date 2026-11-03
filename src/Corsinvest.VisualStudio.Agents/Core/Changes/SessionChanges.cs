/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Sessions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>Runs before a file is copied, to get unsaved editor changes onto disk: the copy must
    /// be of what Claude is about to change, not of a stale file. Null = nothing to do.</summary>
    public Func<string, Task> PrepareFile { get; set; }

    /// <summary>The waits before each look for the CLI's delta after a hook. The record is written
    /// about 100 ms after the hook (measured), so most edits are found on the second try.</summary>
    public int[] PollMilliseconds { get; set; } = { 0, 120, 250, 500, 1000, 2000, 4000 };

    /// <summary>Where this extension's own copies go. Null = the running extension's folder.</summary>
    public ChangeBaselines Baselines { get; set; }
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

    public SessionChanges(ClaudePaths paths, string workingDirectory, SessionChangesOptions options, OutputWindowLogger log)
    {
        _paths = paths;
        _workingDirectory = workingDirectory;
        _options = options ?? new SessionChangesOptions();
        _log = log ?? OutputWindowLogger.Global;
        _baselines = _options.Baselines ?? ChangeBaselines.Default;
        if (_baselines == ChangeBaselines.Default && Interlocked.Exchange(ref _swept, 1) == 0) { _ = Task.Run(() => _baselines.Sweep(SweepAge)); }
    }

    public ChangeSet Set => _set;

    private SessionManager Sessions => new(_paths, _workingDirectory, _log);

    /// <summary>A different conversation is starting in this pane (New, History pick, /clear): the old
    /// list is not this one's. <paramref name="resume"/> rebuilds it from the CLI's history.</summary>
    public void BeginSession(string sessionId, bool resume)
    {
        ReleaseAll(_set.Reset(sessionId));
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
    }

    /// <summary>The hook that runs before an edit. Takes the copy the CLI will not: only for a
    /// sub-agent, or for a file the user just kept or undid. Bounded by the caller, because the CLI
    /// waits for this answer before it runs the tool.</summary>
    public async Task OnPreEditAsync(HookEdit edit)
    {
        try
        {
            if (!Active(edit)) { return; }
            var path = ChangePaths.Normalize(edit.FilePath, string.IsNullOrEmpty(edit.Cwd) ? _workingDirectory : edit.Cwd);
            if (path is null || _set.TryGet(path, out _)) { return; }
            if (!edit.IsSubAgent && !_set.WantsCapture(path)) { return; }

            ReleaseAll(_set.PrunePending(PendingLifetime));
            if (_options.PrepareFile is not null) { await _options.PrepareFile(path).ConfigureAwait(false); }
            var sessionId = _set.SessionId;
            var baseline = await _baselines.CaptureAsync(sessionId, path).ConfigureAwait(false);
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
        if (_set.TakePending(edit.ToolUseId, path, out var captured)) { found = new Found(captured, false); }
        else { found = await FindCliBaselineAsync(edit, path).ConfigureAwait(false); }

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
        if (_set.Add(row))
        {
            _set.ConsumeCaptureNext(path);
            return;
        }
        _baselines.Release(found.Baseline);
        if (!alreadyFull) { _log.Warn($"[changes] more than {ChangeSet.MaxFiles} files changed: the rest are not listed"); }
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
        if (!_set.TryGet(path, out var row)) { return; }
        var evaluation = await ChangeEvaluator.EvaluateAsync(path, row.Baseline).ConfigureAwait(false);
        if (evaluation.Unchanged)
        {
            var removed = _set.Remove(path);
            if (removed is not null) { _baselines.Release(removed.Baseline); }
            _set.SetCaptureNext(path);
            return;
        }

        _set.Update(path, r =>
        {
            Apply(r, evaluation, r.BaselineMissing);
            if (toolUseId is null) { return; }
            r.LastToolUseId = toolUseId;
            r.LastChangedUtc = DateTime.UtcNow;
        });
    }

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
            var rows = new List<FileChange>();
            var history = await sessions.ReadFileHistoryAsync(sessionId).ConfigureAwait(false);
            foreach (var kv in history.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (generation != Volatile.Read(ref _generation) || _disposed) { break; }
                var baseline = ToFound(sessions, sessionId, kv.Value.Earliest);
                var evaluation = await ChangeEvaluator.EvaluateAsync(kv.Key, baseline.Baseline).ConfigureAwait(false);
                if (evaluation.Unchanged) { continue; }
                rows.Add(NewRow(kv.Key, baseline, evaluation, null));
                if (rows.Count > ChangeSet.MaxFiles) { break; }
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

    private bool Active(HookEdit edit)
    {
        if (_disposed) { return false; }
        if (!_options.FileCheckpoints())
        {
            _set.SetDisabled(NoCheckpoints);
            return false;
        }
        _set.SetDisabled(null);
        EnsureSession(edit.SessionId);
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
    }
}

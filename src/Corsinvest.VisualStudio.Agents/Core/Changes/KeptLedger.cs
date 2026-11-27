/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The files a session's person has accepted with Keep, remembered across a restart of Visual Studio.
/// <para>Resuming a session rebuilds the Changes list from the CLI's own history, which knows what Claude touched and nothing
/// of what the person decided: a file already accepted would come back as a change. So Keep records a stamp of the file as it
/// is (its length and a hash of its bytes), and a rebuilt row whose file still carries that stamp is left out. A file that
/// has changed since carries another and is listed as before, against the CLI's baseline: remembering the accepted content
/// itself, to compare against, would take a copy of every kept file for as long as the session might be resumed, for the sake
/// of showing less of what was already shown.</para>
/// <para>One file per session under <see cref="AppPaths.ChangesFolder"/>, written a moment after the last Keep and when the
/// pane closes. A file that cannot be read or parsed is the same as no file: the worst that costs is the old behaviour.</para>
/// <para>Everything that touches the ledger's file is asynchronous and runs one operation after the other, in the order it was
/// asked for: <see cref="Open"/> returns at once and the read of the session's file is queued, and what is asked of the ledger
/// afterwards (<see cref="IsStillKeptAsync"/>, <see cref="FlushAsync"/>) waits for it.</para></summary>
internal sealed class KeptLedger : IDisposable
{
    private const int Version = 1;

    /// <summary>The stamp of a file that is not there: accepting a deletion is a Keep too, and a deleted file would come back
    /// after a restart as a row unless it is remembered as gone.</summary>
    public const string Absent = "absent";

    /// <summary>A file larger than this is not stamped: reading it all would hold the Keep up for seconds, for a file that is
    /// rarely a thing Claude edited.</summary>
    public const long MaxStampBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    // How long closing the pane waits for the last write: a few milliseconds when nothing is wrong.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(3);

    private readonly string _folder;
    private readonly OutputWindowLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _kept = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionId;
    private bool _dirty;
    // Moves with every change of what is kept, so a write that finished after another Keep does not mark that Keep as written.
    private long _version;
    private Timer _saveTimer;
    private bool _disposed;
    private Task _tail = Task.CompletedTask;

    public KeptLedger(string folder, OutputWindowLogger log)
    {
        _folder = folder;
        _log = log ?? OutputWindowLogger.Global;
    }

    /// <summary>Follow <paramref name="sessionId"/>: what was kept in it before is read back. The same session again changes
    /// nothing. A null id is a chat with no session yet (a new one, before its first message): it leaves the session it
    /// was in, and what is kept meanwhile is carried into the session it gets, which is then the one that has it. A different
    /// session starts from what its own file says, which is nothing when it has none. Returns at once: the file is read in
    /// the background, ahead of anything queued after it.</summary>
    public void Open(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) { sessionId = null; }
        lock (_gate)
        {
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) { return; }
            if (_sessionId is not null)
            {
                // The session being left keeps what was kept in it, written before the next one is read.
                var leaving = _dirty ? Snapshot() : null;
                var file = PathFor(_sessionId);
                var left = _sessionId;
                if (leaving is not null && file is not null) { _ = Enqueue(() => WriteAsync(file, left, leaving)); }
                _kept.Clear();
                _dirty = false;
            }
            _sessionId = sessionId;
            _version++;
            if (sessionId is not null) { _ = Enqueue(() => LoadAsync(sessionId)); }
        }
    }

    /// <summary>The file, as it is now, has been accepted. Never throws: a ledger that cannot write is a Keep that is
    /// forgotten at the next restart, not one that fails.</summary>
    public async Task KeepAsync(string path)
    {
        try
        {
            var stamp = await StampAsync(path).ConfigureAwait(false);
            if (stamp is null)
            {
                _log.Warn($"[changes] {path} could not be read just now, or is larger than {MaxStampBytes >> 20} MB: keeping it will not outlast a restart of Visual Studio");
                return;
            }
            lock (_gate)
            {
                if (_disposed) { return; }
                _kept[path] = stamp;
                _dirty = true;
                _version++;
            }
            ScheduleSave();
        }
        catch (Exception ex)
        {
            _log.Warn($"[changes] could not remember that {path} was kept: {ex.Message}");
        }
    }

    /// <summary>The file is still as it was when it was accepted. False when it was never accepted, has changed since, or cannot
    /// be read. Waits for the session's file to have been read.</summary>
    public async Task<bool> IsStillKeptAsync(string path)
    {
        Task settled;
        lock (_gate) { settled = _tail; }
        await settled.ConfigureAwait(false);

        string recorded;
        lock (_gate) { if (!_kept.TryGetValue(path, out recorded)) { return false; } }
        var now = await StampAsync(path).ConfigureAwait(false);
        return now is not null && string.Equals(now, recorded, StringComparison.Ordinal);
    }

    /// <summary>Write what is waiting to be written, now: when the returned task is done, it has been.</summary>
    public Task FlushAsync() => Enqueue(FlushCurrentAsync);

    public void Dispose()
    {
        Timer timer;
        string file;
        Task flush;
        lock (_gate)
        {
            if (_disposed) { return; }
            flush = Enqueue(FlushCurrentAsync);
            _disposed = true;
            timer = _saveTimer;
            _saveTimer = null;
            file = _sessionId is null ? null : PathFor(_sessionId);
        }
        timer?.Dispose();

        // A pane is closing and cannot await: the last write is waited for, with a limit. Nothing in the write returns to the
        // thread that waits here, so it cannot wait on this one.
#pragma warning disable VSTHRD002 // a pane being closed cannot await, and nothing here needs the thread that waits
        try { flush.Wait(CloseWait); }
#pragma warning restore VSTHRD002
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
        {
            _log.Debug(() => $"[changes] the last write of the ledger did not finish: {ex.Message}");
        }

        // The file's age is when the session was last open, not when something was last kept in it: a chat that is worked in
        // for weeks is not one for the sweep to take.
        try { if (file is not null && File.Exists(file)) { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(() => $"[changes] could not touch {file}: {ex.Message}");
        }
    }

    /// <summary>The length and a hash of the file's bytes; <see cref="Absent"/> when there is no such file; null when it cannot
    /// be read (held by another process) or is larger than <paramref name="maxBytes"/>. FNV-1a over 64 bits: it only has to tell
    /// a file from another that someone changed, and the framework's own hashes throw on a machine with FIPS mode on.</summary>
    public static async Task<string> StampAsync(string path, long maxBytes = MaxStampBytes)
    {
        try
        {
            using var stream = await AsyncFile.OpenReadAsync(path).ConfigureAwait(false);
            if (stream.Length > maxBytes) { return null; }
            var hash = 14695981039346656037UL;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                unchecked
                {
                    for (var i = 0; i < read; i++) { hash = (hash ^ buffer[i]) * 1099511628211UL; }
                }
            }
            return $"{stream.Length:x}:{hash:x16}";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Delete the files of sessions nobody has had open for <paramref name="olderThan"/>, and what a crash left of one
    /// being written. Listing a folder and deleting have no async form: a caller on the UI thread hands this to the pool.</summary>
    public static void Sweep(string folder, TimeSpan olderThan, OutputWindowLogger log)
    {
        try
        {
            if (!Directory.Exists(folder)) { return; }
            var limit = DateTime.UtcNow - olderThan;
            // The pattern "*.tmp" also matches a name that merely starts with it (".tmpx"), by the old 8.3 rule: the end is checked.
            var files = Directory.EnumerateFiles(folder, "*.json")
                                 .Concat(Directory.EnumerateFiles(folder, "*.tmp").Where(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                                 .ToList();
            foreach (var file in files)
            {
                if (File.GetLastWriteTimeUtc(file) >= limit) { continue; }
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Warn($"[changes] could not remove {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warn($"[changes] could not sweep {folder}: {ex.Message}");
        }
    }

    private string PathFor(string sessionId)
        => SessionManager.IsSafePathToken(sessionId) ? Path.Combine(_folder, sessionId + ".json") : null;

    // The operations on the ledger's file go one after the other: a write of the session that was left is finished before the
    // read of the one that follows, which might be the same one again. Returns the task of this operation. A failure of an earlier
    // one does not stop the next; each reports its own.
    private Task Enqueue(Func<Task> operation)
    {
        lock (_gate)
        {
            var task = RunAfterAsync(_tail, operation);
            _tail = task;
            return task;
        }
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* it reported itself */ }
        await operation().ConfigureAwait(false);
    }

    // Under the lock.
    private Dictionary<string, string> Snapshot() => new(_kept, StringComparer.OrdinalIgnoreCase);

    private async Task LoadAsync(string sessionId)
    {
        var fromFile = await ReadAsync(PathFor(sessionId)).ConfigureAwait(false);
        bool save;
        lock (_gate)
        {
            // Another session was opened while the file was read: its own read is queued behind this one.
            if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) { return; }
            // What was kept meanwhile, with no session to put it in, wins over what the file says.
            foreach (var pair in fromFile)
            {
                if (!_kept.ContainsKey(pair.Key)) { _kept[pair.Key] = pair.Value; }
            }
            _dirty = _kept.Count > 0;
            _version++;
            save = _dirty;
        }
        if (save) { ScheduleSave(); }
    }

    private async Task<Dictionary<string, string>> ReadAsync(string file)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (file is null) { return kept; }
        try
        {
            string text;
            try { text = await AsyncFile.ReadAllTextAsync(file).ConfigureAwait(false); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return kept; }

            var root = JObject.Parse(text);
            // Anything but the number this writes is another format, or damage: no ledger, as the class says.
            if (!((root["version"] as JValue)?.Value is long version && version == Version)) { return kept; }
            if (root["kept"] is JObject entries)
            {
                foreach (var entry in entries.Properties())
                {
                    var stamp = entry.Value.Type == JTokenType.String ? (string)entry.Value : null;
                    if (!string.IsNullOrEmpty(stamp)) { kept[entry.Name] = stamp; }
                }
            }
        }
        catch (Exception ex)
        {
            // Whatever is wrong with it, this runs where a chat starts: it must not stop the chat from starting.
            _log.Warn($"[changes] could not read {file}, so nothing is remembered from it: {ex.Message}");
            kept.Clear();
        }
        return kept;
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_disposed || _sessionId is null) { return; }
            _saveTimer ??= new Timer(OnSaveTimer, null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSaveTimer(object state) => _ = SaveQuietlyAsync();

    private async Task SaveQuietlyAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn($"[changes] could not write the ledger: {ex.Message}"); }
    }

    private async Task FlushCurrentAsync()
    {
        string sessionId, file;
        Dictionary<string, string> mine;
        long version;
        lock (_gate)
        {
            if (!_dirty || _sessionId is null) { return; }
            sessionId = _sessionId;
            file = PathFor(sessionId);
            if (file is null) { _dirty = false; return; }
            mine = Snapshot();
            version = _version;
        }

        var written = await WriteAsync(file, sessionId, mine).ConfigureAwait(false);
        if (written is null) { return; }

        bool again;
        lock (_gate)
        {
            // Another session was opened while the file was written: what is held now is not what was written.
            if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) { return; }
            // Something may have been kept in this session by another since this one read the file: what it wrote and this
            // does not hold is carried over, not dropped.
            foreach (var pair in written)
            {
                if (!_kept.ContainsKey(pair.Key)) { _kept[pair.Key] = pair.Value; }
            }
            // A Keep that came while the file was written is not in it: still to be written.
            again = _version != version;
            _dirty = again;
        }
        if (again) { ScheduleSave(); }
    }

    // What is written is what is held and what the file already has, a path both hold keeping the stamp held, the later. The
    // whole, or null when it could not be written.
    private async Task<Dictionary<string, string>> WriteAsync(string file, string sessionId, Dictionary<string, string> mine)
    {
        // A name of its own: two writers of one session (two chats, two Visual Studios) must not write the same temporary file.
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() => Directory.CreateDirectory(_folder)).ConfigureAwait(false);

            foreach (var pair in await ReadAsync(file).ConfigureAwait(false))
            {
                if (!mine.ContainsKey(pair.Key)) { mine[pair.Key] = pair.Value; }
            }

            var json = new JObject
            {
                ["version"] = Version,
                ["sessionId"] = sessionId,
                ["kept"] = new JObject(mine.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new JProperty(p.Key, p.Value))),
            }.ToIndentedString();

            // Written beside and moved over, so a crash in the middle leaves the old file and not half of the new one.
            await AsyncFile.WriteAllTextAsync(temp, json, new UTF8Encoding(false)).ConfigureAwait(false);
            await Task.Run(() =>
            {
                if (File.Exists(file)) { File.Replace(temp, file, null); }
                else { File.Move(temp, file); }
            }).ConfigureAwait(false);
            return mine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"[changes] could not write {file}: {ex.Message}");
            try { await Task.Run(() => File.Delete(temp)).ConfigureAwait(false); }
            catch (Exception) { /* left for the sweep */ }
            return null;
        }
    }
}

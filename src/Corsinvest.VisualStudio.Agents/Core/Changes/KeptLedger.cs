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
/// pane closes. A file that cannot be read or parsed is the same as no file: the worst that costs is the old behaviour.</para></summary>
internal sealed class KeptLedger : IDisposable
{
    private const int Version = 1;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _folder;
    private readonly OutputWindowLogger _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _kept = new(StringComparer.OrdinalIgnoreCase);
    private string _sessionId;
    private bool _dirty;
    private Timer _saveTimer;
    private bool _disposed;

    public KeptLedger(string folder, OutputWindowLogger log)
    {
        _folder = folder;
        _log = log ?? OutputWindowLogger.Global;
    }

    /// <summary>Follow <paramref name="sessionId"/>: what was kept in it before is read back. The same session again changes
    /// nothing. A null id is a chat with no session yet (a new one, before its first message): it leaves the session it
    /// was in, and what is kept meanwhile is carried into the session it gets, which is then the one that has it. A different
    /// session starts from what its own file says, which is nothing when it has none.</summary>
    public void Open(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) { sessionId = null; }
        bool save;
        lock (_gate)
        {
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) { return; }
            if (_sessionId is not null)
            {
                FlushLocked();
                _kept.Clear();
                _dirty = false;
            }
            _sessionId = sessionId;
            if (sessionId is not null)
            {
                // What was kept meanwhile, with no session to put it in, wins over what the file says.
                foreach (var pair in Read(PathFor(sessionId)))
                {
                    if (!_kept.ContainsKey(pair.Key)) { _kept[pair.Key] = pair.Value; }
                }
            }
            _dirty = sessionId is not null && _kept.Count > 0;
            save = _dirty;
        }
        if (save) { ScheduleSave(); }
    }

    /// <summary>The file, as it is now, has been accepted. Never throws: a ledger that cannot write is a Keep that is
    /// forgotten at the next restart, not one that fails.</summary>
    public async Task KeepAsync(string path)
    {
        try
        {
            var stamp = await Task.Run(() => Stamp(path)).ConfigureAwait(false);
            if (stamp is null) { return; }
            lock (_gate)
            {
                if (_disposed) { return; }
                _kept[path] = stamp;
                _dirty = true;
            }
            ScheduleSave();
        }
        catch (Exception ex)
        {
            _log.Warn($"[changes] could not remember that {path} was kept: {ex.Message}");
        }
    }

    /// <summary>The file is still as it was when it was accepted. False when it was never accepted, has changed since, or cannot
    /// be read. Reads the file, so not for the UI thread.</summary>
    public bool IsStillKept(string path)
    {
        string recorded;
        lock (_gate) { if (!_kept.TryGetValue(path, out recorded)) { return false; } }
        var now = Stamp(path);
        return now is not null && string.Equals(now, recorded, StringComparison.Ordinal);
    }

    /// <summary>A path is no longer accepted: the file was changed after the Keep, or put back.</summary>
    public void Forget(string path)
    {
        lock (_gate)
        {
            if (!_kept.Remove(path)) { return; }
            _dirty = true;
        }
        ScheduleSave();
    }

    /// <summary>Write what is waiting to be written, now.</summary>
    public void Flush()
    {
        lock (_gate) { FlushLocked(); }
    }

    public void Dispose()
    {
        Timer timer;
        lock (_gate)
        {
            if (_disposed) { return; }
            FlushLocked();
            _disposed = true;
            timer = _saveTimer;
            _saveTimer = null;
        }
        timer?.Dispose();
    }

    /// <summary>The length and a hash of the file's bytes, or null when it cannot be read. FNV-1a over 64 bits: it only has to tell
    /// a file from another that someone changed, and the framework's own hashes throw on a machine with FIPS mode on.</summary>
    public static string Stamp(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = 14695981039346656037UL;
            var buffer = new byte[64 * 1024];
            int read;
            unchecked
            {
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < read; i++) { hash = (hash ^ buffer[i]) * 1099511628211UL; }
                }
            }
            return stream.Length.ToString("x") + ":" + hash.ToString("x16");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Delete the files of sessions nobody has kept anything in for <paramref name="olderThan"/>.</summary>
    public static void Sweep(string folder, TimeSpan olderThan, OutputWindowLogger log)
    {
        try
        {
            if (!Directory.Exists(folder)) { return; }
            var limit = DateTime.UtcNow - olderThan;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
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

    private Dictionary<string, string> Read(string file)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (file is null || !File.Exists(file)) { return kept; }
        try
        {
            var root = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
            if (root.Value<int?>("version") != Version) { return kept; }
            if (root["kept"] is JObject entries)
            {
                foreach (var entry in entries.Properties())
                {
                    var stamp = entry.Value.Type == JTokenType.String ? (string)entry.Value : null;
                    if (!string.IsNullOrEmpty(stamp)) { kept[entry.Name] = stamp; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            _log.Debug(() => $"[changes] could not read {file}, so nothing is remembered from it: {ex.Message}");
        }
        return kept;
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_disposed || _sessionId is null) { return; }
            _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushLocked()
    {
        if (!_dirty || _sessionId is null) { return; }
        var file = PathFor(_sessionId);
        if (file is null) { _dirty = false; return; }
        try
        {
            Directory.CreateDirectory(_folder);
            var json = new JObject
            {
                ["version"] = Version,
                ["sessionId"] = _sessionId,
                ["kept"] = new JObject(_kept.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => new JProperty(p.Key, p.Value))),
            }.ToIndentedString();

            // Written beside and moved over, so a crash in the middle leaves the old file and not half of the new one.
            var temp = file + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(file)) { File.Replace(temp, file, null); }
            else { File.Move(temp, file); }
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"[changes] could not write {file}: {ex.Message}");
        }
    }
}

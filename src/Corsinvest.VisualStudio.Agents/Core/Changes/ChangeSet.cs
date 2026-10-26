/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The files Claude changed in one pane's session: the list the Changes panel shows.
/// <para>Thread-safe, because the hook callbacks that feed it arrive on the CLI's reader thread while
/// the panel reads it from the UI. It does no I/O of its own: what has to be deleted when a row
/// goes (a copy this extension took) is handed back to the caller.</para></summary>
internal sealed class ChangeSet
{
    /// <summary>Rows beyond this are dropped and <see cref="Overflow"/> says so. Two hundred is more
    /// than anyone reviews one by one, and it bounds the work a refresh does.</summary>
    public const int MaxFiles = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, FileChange> _rows = new(StringComparer.OrdinalIgnoreCase);

    // Paths whose next edit must be compared with a copy taken just before it: after a Keep or an
    // Undo the CLI has no backup to offer for "now", and a copy taken later than the edit would
    // already contain it.
    private readonly HashSet<string> _captureNext = new(StringComparer.OrdinalIgnoreCase);

    // Copies taken at a PreToolUse, waiting for their PostToolUse. Keyed by tool call, not path:
    // a copy for an edit that never landed must not become the baseline of a later, unrelated one.
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed class Pending
    {
        public string Path;
        public BaselineRef Baseline;
        public DateTime AtUtc;
    }

    private string _sessionId;
    private bool _overflow;
    private string _disabledReason;

    /// <summary>Raised after any change to the rows or flags, on the thread that made it, outside the lock.</summary>
    public event Action Changed;

    public string SessionId { get { lock (_gate) { return _sessionId; } } }
    public bool Overflow { get { lock (_gate) { return _overflow; } } }

    /// <summary>Why nothing is being tracked, or null when something is.</summary>
    public string DisabledReason { get { lock (_gate) { return _disabledReason; } } }

    public bool TryGet(string path, out FileChange row)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(path, out var found))
            {
                row = found.Clone();
                return true;
            }
        }
        row = null;
        return false;
    }

    /// <summary>A copy of every row, by path. The copies are the caller's: nothing here changes under it.</summary>
    public IReadOnlyList<FileChange> Snapshot()
    {
        lock (_gate)
        {
            return _rows.Values
                        .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(r => r.Clone())
                        .ToList();
        }
    }

    public int Count { get { lock (_gate) { return _rows.Count; } } }

    /// <summary>Add a row. False when the set is full, which also raises <see cref="Overflow"/>; a
    /// path already present is left as it is (its baseline is the first one, by design).</summary>
    public bool Add(FileChange row)
    {
        bool added;
        lock (_gate)
        {
            if (_rows.ContainsKey(row.Path)) { return true; }
            if (_rows.Count >= MaxFiles)
            {
                _overflow = true;
                added = false;
            }
            else
            {
                _rows[row.Path] = row.Clone();
                added = true;
            }
        }
        RaiseAfter();
        return added;
    }

    /// <summary>Change a row in place, if there is one.</summary>
    public bool Update(string path, Action<FileChange> change)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(path, out var row)) { return false; }
            change(row);
        }
        RaiseAfter();
        return true;
    }

    /// <summary>Drop a row and return it, so the caller can release what it owned.</summary>
    public FileChange Remove(string path)
    {
        FileChange removed;
        lock (_gate)
        {
            if (!_rows.TryGetValue(path, out removed)) { return null; }
            _rows.Remove(path);
            _overflow &= _rows.Count >= MaxFiles;
        }
        RaiseAfter();
        return removed;
    }

    public void SetCaptureNext(string path)
    {
        lock (_gate) { _captureNext.Add(path); }
    }

    /// <summary>True, once, if the path was marked by <see cref="SetCaptureNext"/>.</summary>
    public bool ConsumeCaptureNext(string path)
    {
        lock (_gate) { return _captureNext.Remove(path); }
    }

    public bool WantsCapture(string path)
    {
        lock (_gate) { return _captureNext.Contains(path); }
    }

    public void StorePending(string toolUseId, string path, BaselineRef baseline)
    {
        if (string.IsNullOrEmpty(toolUseId)) { return; }
        lock (_gate)
        {
            _pending[toolUseId] = new Pending { Path = path, Baseline = baseline, AtUtc = DateTime.UtcNow };
        }
    }

    /// <summary>The copy taken before this tool call, if any. Consumed: it is the baseline of one edit.</summary>
    public bool TakePending(string toolUseId, string path, out BaselineRef baseline)
    {
        lock (_gate)
        {
            if (toolUseId is not null
                && _pending.TryGetValue(toolUseId, out var pending)
                && ChangePaths.Same(pending.Path, path))
            {
                _pending.Remove(toolUseId);
                baseline = pending.Baseline;
                return true;
            }
        }
        baseline = BaselineRef.None;
        return false;
    }

    /// <summary>Forget copies nobody came back for (the edit was refused or failed). Returns them to release.</summary>
    public List<BaselineRef> PrunePending(TimeSpan olderThan)
    {
        var released = new List<BaselineRef>();
        lock (_gate)
        {
            var limit = DateTime.UtcNow - olderThan;
            foreach (var key in _pending.Where(kv => kv.Value.AtUtc < limit).Select(kv => kv.Key).ToList())
            {
                released.Add(_pending[key].Baseline);
                _pending.Remove(key);
            }
        }
        return released;
    }

    /// <summary>Start over for <paramref name="sessionId"/>. Returns every baseline the old rows owned,
    /// for the caller to release.</summary>
    public List<BaselineRef> Reset(string sessionId)
    {
        var released = new List<BaselineRef>();
        lock (_gate)
        {
            released.AddRange(_rows.Values.Select(r => r.Baseline));
            released.AddRange(_pending.Values.Select(p => p.Baseline));
            _rows.Clear();
            _captureNext.Clear();
            _pending.Clear();
            _overflow = false;
            _sessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId;
        }
        RaiseAfter();
        return released;
    }

    /// <summary>Follow the session a hook says it belongs to. An unknown session adopts the id with
    /// no reset (rows recorded before the id was known belong to it); a different one starts over.
    /// Returns whether it did, and the baselines to release.</summary>
    public bool EnsureSession(string sessionId, out List<BaselineRef> released)
    {
        released = null;
        if (string.IsNullOrEmpty(sessionId)) { return false; }
        lock (_gate)
        {
            if (_sessionId is null)
            {
                _sessionId = sessionId;
                return false;
            }
            if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal)) { return false; }
        }
        released = Reset(sessionId);
        return true;
    }

    public void SetDisabled(string reason)
    {
        lock (_gate)
        {
            if (string.Equals(_disabledReason, reason, StringComparison.Ordinal)) { return; }
            _disabledReason = reason;
        }
        RaiseAfter();
    }

    private void RaiseAfter() => Changed?.Invoke();
}

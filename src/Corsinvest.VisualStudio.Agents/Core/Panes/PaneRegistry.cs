/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// Process-wide registry of all live session panes (CLI and Chat). One collection
/// is the single source of truth: toolbar binds filtered views by kind, MCP lifecycle
/// keys off the total count (first in → start, last out → stop) via the events below.
/// All mutators must run on the UI thread (WPF raises collection-changed there).
/// </summary>
public sealed class PaneRegistry
{
    private static readonly Lazy<PaneRegistry> _instance = new(() => new PaneRegistry());
    public static PaneRegistry Instance => _instance.Value;

    private PaneRegistry() { }

    /// <summary>Every live session, both kinds, in insertion order.</summary>
    public ObservableCollection<PaneEntry> Entries { get; } = [];

    /// <summary>Fired when the collection goes empty → non-empty (first session of the VS
    /// instance opened). The package hooks this to start the MCP server lazily.</summary>
    public event Action FirstSessionStarted;

    /// <summary>Fired when the last session closes (back to empty); the package stops MCP.</summary>
    public event Action LastSessionEnded;

    /// <summary>Append a freshly-created entry, notify, and raise lifecycle events.
    /// No dedupe: every RegisterInstance mints a distinct entry (unique <see cref="PaneEntry.SeqNo"/>),
    /// removed by exact instance. Identity must NOT key off PaneId — VS recycles
    /// MultiInstanceToolNum, so two live panes can briefly share one and a PaneId-keyed
    /// dedupe would silently evict a still-open sibling.</summary>
    public PaneEntry Add(PaneEntry entry)
    {
        var wasEmpty = Entries.Count == 0;
        Entries.Add(entry);
        if (wasEmpty && Entries.Count > 0)
        {
            OutputWindowLogger.Info("[registry] first session started — MCP server will start");
            FirstSessionStarted?.Invoke();
        }
        return entry;
    }

    /// <summary>Remove this exact entry instance (identity removal stays unambiguous
    /// even when PaneIds were recycled). Fires LastSessionEnded when the collection empties.</summary>
    public void Remove(PaneEntry entry)
    {
        if (entry == null || !Entries.Contains(entry)) { return; }
        Entries.Remove(entry);
        if (Entries.Count == 0)
        {
            OutputWindowLogger.Info("[registry] last session ended — MCP server will stop");
            LastSessionEnded?.Invoke();
        }
    }

    /// <summary>Close every live pane via its CloseAction (which disposes the pane and
    /// removes the entry). Used on solution close, since panes are bound to its workdir.
    /// Snapshot first — CloseAction mutates Entries.</summary>
    public void CloseAll()
    {
        foreach (var entry in Entries.ToArray())
        {
            try { entry.CloseAction?.Invoke(); }
            catch (Exception ex) { OutputWindowLogger.LogException("PaneRegistry.CloseAll", ex); }
        }
    }

    /// <summary>Live entries of one kind — what the toolbar open-panes list binds to.</summary>
    public IEnumerable<PaneEntry> OfKind(PaneKind kind) => Entries.Where(e => e.Kind == kind);

    public PaneEntry Find(PaneKind kind, int paneId)
        => Entries.FirstOrDefault(e => e.PaneId == paneId && e.Kind == kind);
}

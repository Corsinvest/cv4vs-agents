/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>Draws the user's attention to a pane that needs input or has just finished, when they're
/// not looking at it. Always shows an InfoBar on the VS main window (with a "Go to pane" action);
/// when VS doesn't have the OS focus, also raises an OS toast — layout-proof, so it reaches the user
/// in another app / tile / monitor where the InfoBar alone would be missed. No-op when the pane is
/// already the active frame. Chat panes only for now.</summary>
internal static class PaneAttentionService
{
    // One live InfoBar per pane (by SeqNo) so repeated events replace instead of stacking.
    private static readonly Dictionary<int, PaneAttentionInfoBar> _bars = [];

    /// <summary>A pane is asking for input (blocking). Always notifies (unless the pane is active):
    /// the model is waiting on the user.</summary>
    public static void NotifyInput(PaneWindowBase pane, PaneEntry entry)
        => Notify(pane, entry, $"Chat #{entry.SeqNo} needs your input", isError: true);

    /// <summary>A pane finished its turn. Notifies only when the user isn't on that pane.</summary>
    public static void NotifyFinished(PaneWindowBase pane, PaneEntry entry)
        => Notify(pane, entry, $"Chat #{entry.SeqNo} finished", isError: false);

    private static void Notify(PaneWindowBase pane, PaneEntry entry, string message, bool isError)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (pane == null || entry == null) { return; }

        var vsForeground = Win32Focus.IsVsForeground();
        // The user is actually looking at this pane ONLY if it's the active frame AND VS has the OS
        // focus. IsActiveFrame alone stays true when the user switches to another app (VS keeps its
        // last-active frame), so on its own it would wrongly suppress the notification.
        if (vsForeground && pane.IsActiveFrame()) { return; }

        // Show/replace the InfoBar (seen in-VS, and waiting when the user returns).
        var seq = entry.SeqNo;
        if (_bars.TryGetValue(seq, out var existing)) { existing.Close(); }
        var bar = PaneAttentionInfoBar.TryShow(
            message,
            isError,
            onGoTo: () => entry.ActivateAction?.Invoke(),
            onClosed: () => _bars.Remove(seq));
        if (bar != null) { _bars[seq] = bar; }

        // VS not focused → the InfoBar can be missed (other app / tile / monitor). OS toast reaches
        // the user regardless of layout; clicking it brings VS to the front and activates the pane.
        if (!vsForeground)
        {
            Win32Focus.ShowToast("Claude Code", message, onClick: () => entry.ActivateAction?.Invoke());
        }
    }

    /// <summary>Dismiss any attention InfoBar for this pane (e.g. the user answered / navigated to it).</summary>
    public static void Clear(PaneEntry entry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (entry != null && _bars.TryGetValue(entry.SeqNo, out var bar))
        {
            bar.Close();
            _bars.Remove(entry.SeqNo);
        }
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Workspace;

/// <summary>Which of the saved panes a restore still has to open, given the ones already there.
/// Panes are already there after a solution reload, which keeps them alive, and when the restore
/// is asked for twice: the package start and the solution-open event can both ask.</summary>
internal static class RestorePlan
{
    /// <summary>The entries of <paramref name="saved"/> with no pane behind them yet, in saved order.
    /// Each open pane accounts for one saved entry, so two fresh chats saved and one still open
    /// leaves one to reopen. A session is opened once however many times it was saved: two
    /// claude.exe on one session write the same .jsonl.</summary>
    public static List<PaneState> ToOpen(IEnumerable<PaneState> saved, IEnumerable<PaneState> open)
    {
        var unclaimed = open.ToList();
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toOpen = new List<PaneState>();

        foreach (var pane in saved)
        {
            var hasSession = !string.IsNullOrEmpty(pane.SessionId);
            // A pane with no session has nothing to be told apart by but its kind.
            var at = unclaimed.FindIndex(o => hasSession
                ? string.Equals(o.SessionId, pane.SessionId, StringComparison.OrdinalIgnoreCase)
                : string.IsNullOrEmpty(o.SessionId) && string.Equals(o.Kind, pane.Kind, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                unclaimed.RemoveAt(at);
                if (hasSession) { sessions.Add(pane.SessionId); }
                continue;
            }
            if (hasSession && !sessions.Add(pane.SessionId)) { continue; }
            toOpen.Add(pane);
        }
        return toOpen;
    }
}

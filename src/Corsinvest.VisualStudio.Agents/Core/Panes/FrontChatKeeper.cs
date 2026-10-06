/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary><para>
/// Keeps the chat the user is looking at in front while the shell swaps window layouts. Visual
/// Studio holds one layout for design time and one for run time, each with its own front tab per
/// group, so starting or stopping a program puts forward whichever chat was in front the last
/// time that layout was used. Neither is a request to look at another conversation.
/// </para>
/// <para>
/// The frames are reached through the three delegates, so the rule (when to note, when to put
/// back, when to stop) can be tested without a shell.
/// </para></summary>
internal sealed class FrontChatKeeper(Func<IEnumerable<PaneEntry>> openChats,
                                      Func<PaneEntry, bool> isFront,
                                      Action<PaneEntry> bringForward)
{
    // The chats to keep in front while a swap is under way; null the rest of the time.
    private List<PaneEntry> _kept;
    private bool _inDesign = true;

    /// <summary>The debugger changed mode. Run and Break share the run-time layout, so only crossing
    /// into or out of design swaps it: noting the front tabs on a Run to Break change would record
    /// what the swap just did instead of what the user had. Call it before the shell swaps, which
    /// is when the debugger's own event arrives.</summary>
    public void ModeChanged(bool inDesign)
    {
        var swaps = inDesign != _inDesign;
        _inDesign = inDesign;
        if (!swaps) { return; }

        var front = openChats().Where(isFront).ToList();
        _kept = front.Count > 0 ? front : null;
    }

    /// <summary>Whether a swap is under way with chats to keep in front.</summary>
    public bool Keeping => _kept != null;

    /// <summary>Put the noted chats back in front, those the swap left behind a sibling's tab.
    /// One closed in the meantime is left alone: nothing should bring it back.</summary>
    public void Reassert()
    {
        if (_kept == null) { return; }
        var open = openChats().ToList();
        foreach (var entry in _kept.Where(e => open.Contains(e) && !isFront(e)))
        {
            bringForward(entry);
        }
    }

    /// <summary>The swap is over: one last time, then stop, so a tab the user picks from now on
    /// stays picked.</summary>
    public void Finish()
    {
        Reassert();
        _kept = null;
    }
}

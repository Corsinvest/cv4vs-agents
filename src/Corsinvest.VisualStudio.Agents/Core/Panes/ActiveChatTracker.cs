/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>Which chat pane the user was in last. Remembered, not asked for when needed: a prompt
/// sent from an editor menu arrives with the editor as the active frame, so by then no chat is.
/// Apart from <see cref="PaneRegistry"/>, which feeds it from the shell, so the rule can be tested
/// without one.</summary>
internal sealed class ActiveChatTracker
{
    private PaneEntry _last;

    /// <summary>A pane became the active window frame. Only a chat is kept: going through a CLI
    /// pane on the way to the editor must not lose the chat that was in use.</summary>
    public void Activated(PaneEntry entry)
    {
        if (entry?.Kind == PaneKind.Chat) { _last = entry; }
    }

    /// <summary>A pane closed. Its entry keeps a composer action that now reaches a disposed
    /// WebView, so it must not be handed out again.</summary>
    public void Removed(PaneEntry entry)
    {
        if (ReferenceEquals(_last, entry)) { _last = null; }
    }

    /// <summary>The chat the user was in last, if <paramref name="usable"/> accepts it, else the
    /// newest of <paramref name="chats"/> that it does: none has been clicked since the solution
    /// restored them, or the one that was has been closed.</summary>
    public PaneEntry Pick(IEnumerable<PaneEntry> chats, Func<PaneEntry, bool> usable)
        => _last != null && usable(_last) ? _last : chats.LastOrDefault(usable);
}

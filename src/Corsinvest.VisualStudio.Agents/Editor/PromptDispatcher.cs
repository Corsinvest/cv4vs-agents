/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary>Puts a prompt in front of the user: into an open chat pane's composer, or into a new
/// pane when none is open. Shared by every context-menu entry that hands text to the agent.</summary>
internal static class PromptDispatcher
{
    /// <summary>With several panes open it goes to the last activated — the one being worked in,
    /// where the first by SeqNo would just be the oldest — and brings it forward, or the prompt
    /// lands in a tool window nobody is looking at.
    ///
    /// <paramref name="sendImmediately"/> submits the composer the way the send button does, IDE
    /// context and all.</summary>
    public static void Send(string prompt, bool sendImmediately)
    {
        var target = PaneRegistry.Instance.OfKind(PaneKind.Chat).LastOrDefault(e => e.SetComposerAction != null);
        if (target == null)
        {
            // First profile = the native "Claude" that ProfileStore prepends.
            var profile = ProfileStore.Load(forEdit: false).FirstOrDefault();
            if (profile != null)
            {
                PaneLauncher.OpenNew(PaneKind.Chat, profile, initialPrompt: prompt, initialPromptSend: sendImmediately);
            }
            return;
        }

        // Text first, activation second. Bringing the pane forward makes it the active window, and
        // the IDE context that rides along with the turn is read from the active document — so
        // activating first sends the prompt with no file behind it.
        target.SetComposerAction(prompt, sendImmediately);
        target.ActivateAction?.Invoke();
    }
}

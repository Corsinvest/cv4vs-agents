/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Linq;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary>The code window's context menu: one item per configured prompt, written into a chat
/// pane's composer. Same DynamicItemStart mechanism as
/// <see cref="Menu.ActiveSessionsMenuCommand"/>, including its two contract quirks: a matched
/// command records its own id, and the id is cleared afterwards.</summary>
internal sealed class EditorPromptsMenuCommand : OleMenuCommand
{
    private readonly int _baseId;

    private EditorPromptsMenuCommand(CommandID rootId)
        : base(OnInvoke, changeHandler: null, OnBeforeQueryStatus, rootId)
    {
        _baseId = rootId.ID;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        var rootId = new CommandID(PackageGuids.AgentsCommandSet, PackageIds.EditorPromptCommandId);
        commandService?.AddCommand(new EditorPromptsMenuCommand(rootId));
    }

    /// <summary>Matches the seed id plus the whole dynamic range (base..base+Count-1),
    /// which is how VS discovers how many entries to render.</summary>
    public override bool DynamicItemMatch(int cmdId)
    {
        if (cmdId < _baseId || cmdId >= _baseId + EditorPrompts.Items.Count) { return false; }
        MatchedCommandId = cmdId;
        return true;
    }

    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var cmd = (EditorPromptsMenuCommand)sender;
        var items = EditorPrompts.Items;
        var index = GetIndex(cmd);

        if (index < items.Count)
        {
            cmd.Visible = true;
            cmd.Text = items[index].Title;
            // Greyed out rather than hidden: an entry that comes and goes reads as a bug.
            cmd.Enabled = !items[index].RequiresSelection || HasSelection();
        }
        else
        {
            cmd.Enabled = false;
            cmd.Visible = false;
        }

        cmd.MatchedCommandId = 0;
    }

    private static bool HasSelection()
        => IdeContextService.Instance.GetCurrentContext()?.HasSelection == true;

    private static void OnInvoke(object sender, EventArgs e)
    {
        var cmd = (EditorPromptsMenuCommand)sender;
        var items = EditorPrompts.Items;
        var index = GetIndex(cmd);
        cmd.MatchedCommandId = 0;
        if (index < 0 || index >= items.Count) { return; }

        Send(items[index].Prompt);
    }

    /// <summary>Delivers the prompt to a chat pane. With several open it goes to the last
    /// activated — the one being worked in, where the first by SeqNo would just be the oldest —
    /// and brings it forward, or the prompt lands in a tool window nobody is looking at.</summary>
    private static void Send(string prompt)
    {
        var target = PaneRegistry.Instance.OfKind(PaneKind.Chat).LastOrDefault(e => e.SetComposerAction != null);
        if (target == null)
        {
            // First profile = the native "Claude" that ProfileStore prepends.
            var profile = ProfileStore.Load(forEdit: false).FirstOrDefault();
            if (profile != null) { PaneLauncher.OpenNew(PaneKind.Chat, profile, initialPrompt: prompt); }
            return;
        }

        target.ActivateAction?.Invoke();
        target.SetComposerAction(prompt);
    }

    private static int GetIndex(EditorPromptsMenuCommand cmd)
    {
        var matched = cmd.MatchedCommandId;
        return matched == 0 ? 0 : matched - cmd._baseId;
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary><para>A context menu's prompts: one item per configured prompt, handed to a chat pane.
/// One instance per menu — the code window, the Error List, the Output window — each with its own
/// dynamic range, so an item knows its menu by its id: VS routes a context-menu command without
/// saying which menu it came from.</para>
/// <para>
/// Same DynamicItemStart mechanism as <see cref="Menu.ActiveSessionsMenuCommand"/>, including its
/// two contract quirks: a matched command records its own id, and the id is cleared afterwards.
/// </para></summary>
internal sealed class EditorPromptsMenuCommand : OleMenuCommand
{
    private readonly int _baseId;
    private readonly PromptScope _scope;

    private EditorPromptsMenuCommand(CommandID rootId, PromptScope scope)
        : base(OnInvoke, changeHandler: null, OnBeforeQueryStatus, rootId)
    {
        _baseId = rootId.ID;
        _scope = scope;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        Add(commandService, PackageIds.EditorPromptCommandId, PromptScope.Editor);
        Add(commandService, PackageIds.ErrorListPromptCommandId, PromptScope.ErrorList);
        Add(commandService, PackageIds.OutputPromptCommandId, PromptScope.Output);
    }

    private static void Add(OleMenuCommandService commandService, int seedId, PromptScope scope)
        => commandService?.AddCommand(new EditorPromptsMenuCommand(
            new CommandID(PackageGuids.AgentsCommandSet, seedId), scope));

    /// <summary>Matches the seed id plus the whole dynamic range (base..base+Count-1),
    /// which is how VS discovers how many entries to render.</summary>
    public override bool DynamicItemMatch(int cmdId)
    {
        if (cmdId < _baseId || cmdId >= _baseId + EditorPromptStore.Items(_scope).Count) { return false; }
        MatchedCommandId = cmdId;
        return true;
    }

    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (EditorPromptsMenuCommand)sender;
        var items = EditorPromptStore.Items(cmd._scope);
        var index = GetIndex(cmd);

        if (index < items.Count)
        {
            cmd.Visible = true;
            cmd.Text = items[index].Title;
            // Greyed out rather than hidden: an entry that comes and goes reads as a bug.
            cmd.Enabled = cmd._scope == PromptScope.Editor
                ? !items[index].RequiresSelection || IdeContextService.Instance.HasSelection()
                : WindowPayload.For(cmd._scope) != null;
        }
        else
        {
            cmd.Enabled = false;
            cmd.Visible = false;
        }

        cmd.MatchedCommandId = 0;
    }

    /// <summary>The editor's prompt goes alone — the file and selection travel with it through the
    /// IDE context. The other windows' prompts carry what they are about below them.</summary>
    private static void OnInvoke(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (EditorPromptsMenuCommand)sender;
        var items = EditorPromptStore.Items(cmd._scope);
        var index = GetIndex(cmd);
        cmd.MatchedCommandId = 0;
        if (index < 0 || index >= items.Count) { return; }

        var prompt = items[index].Prompt;
        if (cmd._scope != PromptScope.Editor)
        {
            var payload = WindowPayload.For(cmd._scope);
            if (payload == null) { return; }
            prompt = $"{prompt}\n\n{payload}";
        }
        PromptDispatcher.Send(prompt, items[index].SendImmediately);
    }

    private static int GetIndex(EditorPromptsMenuCommand cmd)
    {
        var matched = cmd.MatchedCommandId;
        return matched == 0 ? 0 : matched - cmd._baseId;
    }
}

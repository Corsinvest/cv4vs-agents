/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary>"Add to chat" at the foot of the Error List's and the Output window's submenus: what
/// the window holds, added to the composer as a block of its own and never sent — the counterpart
/// of the editor's "Add selection to chat". One command per window, so each knows its window by its
/// id.</summary>
internal static class WindowAddToChatMenuCommand
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        Add(commandService, PackageIds.ErrorListAddToChatCommandId, PromptScope.ErrorList);
        Add(commandService, PackageIds.OutputAddToChatCommandId, PromptScope.Output);
    }

    private static void Add(OleMenuCommandService commandService, int id, PromptScope scope)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = new OleMenuCommand(
            (_, __) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var payload = WindowPayload.For(scope);
                if (payload != null) { PromptDispatcher.Append(payload); }
            },
            new CommandID(PackageGuids.AgentsCommandSet, id));
        // Greyed out rather than hidden when there is nothing to add, as the prompts above it are.
        cmd.BeforeQueryStatus += (sender, __) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var c = (OleMenuCommand)sender;
            c.Visible = true;
            c.Enabled = WindowPayload.For(scope) != null;
        };
        commandService?.AddCommand(cmd);
    }
}

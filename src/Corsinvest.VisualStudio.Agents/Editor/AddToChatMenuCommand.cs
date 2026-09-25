/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.IO;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary><para>"Add reference to chat" and "Add selection to chat", at the foot of the editor
/// submenu. Both add to the composer and never send: they are used several times, from several
/// files, before the user writes the question.</para>
/// <para>
/// Two entries, not one that picks, because they read different things. The reference is read by
/// the CLI from disk when the turn goes, so it is the current code but misses unsaved edits; the
/// text is the selection as it is on screen, frozen.
/// </para></summary>
internal static class AddToChatMenuCommand
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        commandService?.AddCommand(new OleMenuCommand(
            OnAddReference,
            new CommandID(PackageGuids.AgentsCommandSet, PackageIds.AddReferenceToChatCommandId)));

        var selection = new OleMenuCommand(
            OnAddSelection,
            new CommandID(PackageGuids.AgentsCommandSet, PackageIds.AddSelectionToChatCommandId));
        selection.BeforeQueryStatus += OnBeforeQuerySelection;
        commandService?.AddCommand(selection);
    }

    /// <summary>Greyed out rather than hidden without a selection, the same call the editor prompts
    /// make. The reference needs no such check: without a selection it names the whole file.</summary>
    private static void OnBeforeQuerySelection(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (OleMenuCommand)sender;
        cmd.Visible = true;
        cmd.Enabled = IdeContextService.Instance.HasSelection();
    }

    private static void OnAddReference(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var ctx = IdeContextService.Instance.GetCurrentContext();
        if (ctx == null) { return; }

        PromptDispatcher.Append(new ComposerMention
        {
            Path = ctx.FilePath,
            StartLine = ctx.HasSelection ? ctx.StartLine : null,
            EndLine = ctx.HasSelection ? ctx.EndLine : null,
        });
    }

    /// <summary>Headed by the absolute path and the lines, the form the CLI's own
    /// <c>&lt;ide_selection&gt;</c> uses, so the agent can open the file around it. Fenced: code is
    /// full of characters markdown would eat. The fence is lengthened when the code holds one.</summary>
    private static void OnAddSelection(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var ctx = IdeContextService.Instance.GetCurrentContext();
        if (ctx?.HasSelection != true || string.IsNullOrEmpty(ctx.SelectedText)) { return; }

        var lines = ctx.StartLine == ctx.EndLine ? $"{ctx.StartLine}" : $"{ctx.StartLine}-{ctx.EndLine}";
        var fence = ctx.SelectedText.Contains("```") ? "````" : "```";
        var language = Path.GetExtension(ctx.FilePath).TrimStart('.');
        PromptDispatcher.Append(
            $"{ctx.FilePath}:{lines}\n{fence}{language}\n{ctx.SelectedText.TrimEnd('\r', '\n')}\n{fence}\n");
    }
}

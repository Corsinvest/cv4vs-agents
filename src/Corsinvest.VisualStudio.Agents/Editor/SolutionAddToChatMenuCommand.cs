/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary><para>"Add reference to chat" in Solution Explorer and on a document's tab: the
/// selected files, folders and projects as references in the composer, one per line, never sent.
/// </para>
/// <para>
/// Two commands, not one placed twice, for the reason the Error List and Output have two: a
/// context-menu command is not told which menu it came from, and the two read different things —
/// the tree's selection, the tab's document.
/// </para></summary>
internal static class SolutionAddToChatMenuCommand
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        Add(commandService, PackageIds.SolutionAddReferenceToChatCommandId, FromSolutionExplorer);
        Add(commandService, PackageIds.TabAddReferenceToChatCommandId, FromDocumentTab);
    }

    private static void Add(OleMenuCommandService commandService, int id, Func<ComposerMention[]> read)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = new OleMenuCommand(
            (_, __) =>
            {
                var mentions = read();
                if (mentions.Length > 0) { PromptDispatcher.Append(mentions); }
            },
            new CommandID(PackageGuids.AgentsCommandSet, id));
        // Greyed out rather than hidden, as everywhere else: the solution node, a solution folder,
        // an item with no file behind it have nothing to reference.
        cmd.BeforeQueryStatus += (sender, __) =>
        {
            var c = (OleMenuCommand)sender;
            c.Visible = true;
            c.Enabled = read().Length > 0;
        };
        commandService?.AddCommand(cmd);
    }

    /// <summary>Files, folders and projects, in the order the tree gives them. A project stands for
    /// its folder: the reference is to what is in it, not to the project file.</summary>
    private static ComposerMention[] FromSolutionExplorer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var mentions = new List<ComposerMention>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var items = (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.SelectedItems;
            if (items == null) { return []; }
            foreach (EnvDTE.SelectedItem item in items)
            {
                var path = item.ProjectItem != null
                    ? item.ProjectItem.FileCount > 0 ? item.ProjectItem.FileNames[1] : null
                    : string.IsNullOrEmpty(item.Project?.FullName) ? null : Path.GetDirectoryName(item.Project.FullName);
                if (string.IsNullOrEmpty(path)) { continue; }

                var isFolder = Directory.Exists(path);
                if (!isFolder && !File.Exists(path)) { continue; }
                path = path.TrimEnd('\\', '/');
                if (seen.Add(path)) { mentions.Add(new ComposerMention { Path = path, IsFolder = isFolder }); }
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[editor] could not read the Solution Explorer selection: {ex.Message}");
        }
        return [.. mentions];
    }

    private static ComposerMention[] FromDocumentTab()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var path = (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveDocument?.FullName;
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? [] : [new ComposerMention { Path = path }];
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[editor] could not read the tab's document: {ex.Message}");
            return [];
        }
    }
}

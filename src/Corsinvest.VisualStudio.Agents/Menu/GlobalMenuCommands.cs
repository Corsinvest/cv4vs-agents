/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Menu;

/// <summary>
/// The entries under View → cv4vs Agents that don't belong to any pane: settings, the data
/// folder, the output log, docs and feedback, About. They used to live in a pane's toolbar menu,
/// which meant opening a pane before you could reach them.
/// </summary>
internal static class GlobalMenuCommands
{
    private const string RepoUrl = "https://github.com/Corsinvest/cv4vs-agents";

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService svc)
        {
            OutputWindowLogger.Warn("[menu] no command service — the View entries won't respond");
            return;
        }

        Add(svc, PackageIds.SettingsCommandId,
            () => AgentsPackage.Instance?.ShowOptionPage(typeof(AgentsGeneralPage)));
        Add(svc, PackageIds.DataFolderCommandId, () => ShellHelpers.OpenExternal(AppPaths.DataFolder));
        Add(svc, PackageIds.OutputLogCommandId, OutputWindowLogger.ActivatePane);
        Add(svc, PackageIds.DocumentationCommandId, () => ShellHelpers.OpenExternal(RepoUrl));
        // Template names must match the files in .github/ISSUE_TEMPLATE — GitHub silently ignores
        // an unknown one and opens a blank issue instead.
        Add(svc, PackageIds.ReportBugCommandId,
            () => ShellHelpers.OpenExternal($"{RepoUrl}/issues/new?template=bug_report.yml"));
        Add(svc, PackageIds.RequestFeatureCommandId,
            () => ShellHelpers.OpenExternal($"{RepoUrl}/issues/new?template=feature_request.yml"));
        Add(svc, PackageIds.FeedbackCommandId,
            () => ShellHelpers.OpenExternal($"{RepoUrl}/issues/new?template=feedback.yml"));
        Add(svc, PackageIds.AboutCommandId, () => new AboutDialog().ShowDialog());
    }

    /// <summary>One place for the try/catch: a menu action must never take VS down.</summary>
    private static void Add(OleMenuCommandService svc, int id, Action run)
        => svc.AddCommand(new MenuCommand((_, _) =>
        {
            try { run(); }
            catch (Exception ex) { OutputWindowLogger.LogException($"GlobalMenu.0x{id:X}", ex); }
        }, new CommandID(PackageGuids.AgentsCommandSet, id)));
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Reflection;
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
        // The release for the running version, not the list: /releases/tag/v<version>. A build
        // that was actually shipped has a matching tag; a local build 404s to GitHub's own
        // "release not found" page, which links back to the list.
        Add(svc, PackageIds.ReleasesCommandId,
            () => ShellHelpers.OpenExternal($"{RepoUrl}/releases/tag/v{BuildInfo.Version}"));
        Add(svc, PackageIds.ReportBugCommandId, () => OpenIssue("bug_report.yml", withVersion: true));
        Add(svc, PackageIds.RequestFeatureCommandId, () => OpenIssue("feature_request.yml"));
        Add(svc, PackageIds.FeedbackCommandId, () => OpenIssue("feedback.yml", withVersion: true));
        Add(svc, PackageIds.AboutCommandId, () => new AboutDialog().ShowDialog());
        Add(svc, PackageIds.StatisticsCommandId, Core.Stats.StatisticsDocument.Open);
        Add(svc, PackageIds.UsageCommandId, Core.Usage.UsageDocument.Open);
        Add(svc, PackageIds.ContextUsageCommandId, Core.Context.ContextDocument.Open);
    }

    /// <summary>Open a GitHub issue form. The template name must match a file in
    /// .github/ISSUE_TEMPLATE — GitHub silently ignores an unknown one and opens a blank issue.
    /// Query-string keys match the field ids in the YAML, so GitHub pre-fills them: the version is
    /// where every investigation starts, and asking the user to look it up is how it goes missing.</summary>
    private static void OpenIssue(string template, bool withVersion = false)
    {
        var url = $"{RepoUrl}/issues/new?template={template}";
        if (withVersion)
        {
            url += $"&version={Uri.EscapeDataString(AppVersion())}";
        }
        ShellHelpers.OpenExternal(url);
    }

    private static string AppVersion()
    {
        var asm = typeof(GlobalMenuCommands).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? "";
    }

    /// <summary>One place for the try/catch: a menu action must never take VS down.</summary>
    private static void Add(OleMenuCommandService svc, int id, Action run)
        => svc.AddCommand(new MenuCommand((_, _) =>
        {
            try { run(); }
            catch (Exception ex) { OutputWindowLogger.LogException($"GlobalMenu.0x{id:X}", ex); }
        }, new CommandID(PackageGuids.AgentsCommandSet, id)));
}

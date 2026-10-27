/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Design;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>The two buttons this extension puts in the action panel at the bottom of a reviewed file: the previous and the next file
/// of the Changes list, with Copilot's review where it can be shown, as Open does. The panel, and its Keep, Undo and Keep all, are
/// GitHub Copilot's; the file list it carries is Copilot's own working set, which this extension cannot feed, so these two are the
/// way between the files Claude changed without going back to the chat.
/// <para>Plain <see cref="OleMenuCommand"/>s, parented into the panel's group by <c>AgentsPackage.vsct</c>, which the shell routes
/// here (tried in a Debug build before this was written). They show only while the document the person is in is a review whose
/// list has more than one file.</para></summary>
internal static class FileNavigationCommands
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commands) { return; }

        Add(commands, PackageIds.PreviousFileCommandId, -1);
        Add(commands, PackageIds.NextFileCommandId, 1);
    }

    private static void Add(OleMenuCommandService commands, int id, int delta)
    {
        var command = new OleMenuCommand((sender, _) => Execute(delta), new CommandID(PackageGuids.AgentsCommandSet, id));
        command.BeforeQueryStatus += (sender, _) => Query((OleMenuCommand)sender);
        commands.AddCommand(command);
    }

    private static bool? _lastVisible;

    private static void Query(OleMenuCommand command)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var review = ReviewRegistry.Instance.ReviewForActiveDocument();
        command.Visible = review is not null && review.FilePosition().Count > 1;
        command.Enabled = command.Visible;
        if (_lastVisible != command.Visible)
        {
            _lastVisible = command.Visible;
            OutputWindowLogger.Global.Debug(() => $"[changes] the previous and next file buttons are {(command.Visible ? "shown" : "hidden")} "
                                                  + $"(a review of the active document: {review is not null}, files listed: {review?.FilePosition().Count ?? 0})");
        }
    }

    private static void Execute(int delta)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var review = ReviewRegistry.Instance.ReviewForActiveDocument();
        if (review is null) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(() => review.GoToFileAsync(delta)).FileAndForget("FileNavigationCommands.Execute");
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>The review's commands: Keep and Undo for the hunk the caret is in, Keep and Undo for the whole file, and the move
/// to the next hunk. In the code window's context menu, where they show only while the window is a review, and under key
/// chords in the text editor's scope (<c>AgentsPackage.vsct</c>), which the person can rebind in Tools > Options > Keyboard.
/// <para>Plain <see cref="OleMenuCommand"/>s, not editor command handlers: those are MEF exports, which this extension does
/// not have. The window that has the caret is found through the text manager, so the commands act on the view the person is
/// working in, whichever of the review's views that is.</para></summary>
internal static class HunkCommands
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (await package.GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commands) { return; }

        Add(commands, PackageIds.KeepHunkCommandId, atCaret: true,
            (view, review, hunk) => review.KeepHunkAsync(hunk, view));
        Add(commands, PackageIds.UndoHunkCommandId, atCaret: true,
            (view, review, hunk) => review.UndoHunkAsync(hunk, view));
        Add(commands, PackageIds.KeepAllInFileCommandId, atCaret: false,
            (view, review, _) => review.KeepAllAsync());
        Add(commands, PackageIds.UndoAllInFileCommandId, atCaret: false,
            (view, review, _) => review.UndoAllAsync());
        Add(commands, PackageIds.NextHunkCommandId, atCaret: false,
            (view, review, _) =>
            {
                review.GoToNextHunk(view);
                return Task.CompletedTask;
            });
    }

    // atCaret: the command needs a hunk under the caret, and is greyed out while there is none.
    private static void Add(OleMenuCommandService commands, int id, bool atCaret, Func<IWpfTextView, BaselineDocumentController, Hunk, Task> run)
    {
        var command = new OleMenuCommand((sender, _) => Execute(atCaret, run), new CommandID(PackageGuids.AgentsCommandSet, id));
        command.BeforeQueryStatus += (sender, _) => Query((OleMenuCommand)sender, atCaret);
        commands.AddCommand(command);
    }

    private static void Query(OleMenuCommand command, bool atCaret)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = ActiveView();
        var review = BaselineDocumentRegistry.Instance.ControllerFor(view);
        command.Visible = review is not null;
        command.Enabled = review is not null && !review.IsBusy
                          && (!atCaret || (HunkLocator.TryGetHunkAtCaret(view, out var hunk) && review.IsCurrent(hunk)));
    }

    private static void Execute(bool atCaret, Func<IWpfTextView, BaselineDocumentController, Hunk, Task> run)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var view = ActiveView();
        var review = BaselineDocumentRegistry.Instance.ControllerFor(view);
        if (review is null) { return; }
        var hunk = default(Hunk);
        if (atCaret && !HunkLocator.TryGetHunkAtCaret(view, out hunk)) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(() => run(view, review, hunk)).FileAndForget("HunkCommands.Execute");
    }

    /// <summary>The text view that has the keyboard, or null when it is not a text editor or the review has not started.</summary>
    private static IWpfTextView ActiveView()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var adapters = BaselineDocumentRegistry.Instance.Adapters;
        if (adapters is null || Package.GetGlobalService(typeof(SVsTextManager)) is not IVsTextManager manager) { return null; }
        return manager.GetActiveView(1, null, out var vsView) == VSConstants.S_OK && vsView is not null
            ? adapters.GetWpfTextView(vsView)
            : null;
    }
}

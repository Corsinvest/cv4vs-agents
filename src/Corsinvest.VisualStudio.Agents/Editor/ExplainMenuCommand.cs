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

/// <summary><para>"Explain in cv4vs Agents", in the Output window's and the Error List's context menus.</para>
/// <para>
/// One command on two placements rather than one per window. VS routes a context menu command
/// without saying which menu it came from, so the window is worked out from the active one.
/// </para>
/// <para>
/// Unlike the editor prompts, the text is carried in the prompt itself: neither window is part of
/// the IDE context a chat pane sends (that is editor documents only), so a bare instruction would
/// reach the agent with nothing to explain.
/// </para></summary>
internal static class ExplainMenuCommand
{
    /// <summary>Enough of a build log to hold the error and the lines that led to it, when the
    /// user has selected nothing. Long enough for an MSBuild failure to be in it, short enough
    /// not to spend a turn's context on a pane that has been running all day.</summary>
    private const int OutputTailLines = 80;

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        var id = new CommandID(PackageGuids.AgentsCommandSet, PackageIds.ExplainCommandId);
        var cmd = new OleMenuCommand(OnInvoke, id);
        cmd.BeforeQueryStatus += OnBeforeQueryStatus;
        commandService?.AddCommand(cmd);
    }

    /// <summary>Greyed out rather than hidden when there is nothing to explain — an entry that
    /// comes and goes reads as a bug, the same call the editor prompts make.</summary>
    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cmd = (OleMenuCommand)sender;
        cmd.Visible = true;
        cmd.Enabled = GetText() != null;
    }

    private static void OnInvoke(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var prompt = GetText();
        if (prompt != null) { PromptDispatcher.Send(prompt, sendImmediately: true); }
    }

    /// <summary><para>The whole prompt, or null when there is nothing to explain.</para>
    /// <para>
    /// Asks the window the menu was opened over, not whichever has something to say: with errors
    /// selected in the Error List and the menu opened in the Output window, going by content alone
    /// would explain the errors and never the log the user right-clicked. Falls back to trying
    /// both when the active window is neither — the menu cannot have come from anywhere else, so
    /// this is the docked-and-unfocused case, not a wrong guess.
    /// </para></summary>
    private static string GetText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // By the automation object's type, not by Window.ObjectKind: EnvDTE.Constants predates the
        // Error List and has no window-kind field for it (see docs/internal/envdte-guids.md), so
        // that comparison could only be an unverified GUID literal.
        object active = null;
        try { active = (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ActiveWindow?.Object; }
        catch (Exception ex) { OutputWindowLogger.Global.Warn($"[ide] could not read the active window: {ex.Message}"); }

        if (active is EnvDTE80.ErrorList) { return FromErrorList(); }
        if (active is EnvDTE.OutputWindow) { return FromOutput(); }
        return FromErrorList() ?? FromOutput();
    }

    /// <summary>No fence: the rows are already one structured line each, and a fence would turn
    /// them into an opaque block.</summary>
    private static string FromErrorList()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var text = IdeErrorListService.Instance.GetSelectedText();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : $"What is causing these errors, and how do I fix them?\n\n{text}";
    }

    /// <summary>Fenced, unlike the Error List: a log is full of dashes and asterisks that markdown
    /// would eat. And asked flat — the active pane is as likely to be Debug or the program's own
    /// output as a failed build, so naming a failure would invent one where there is none, and
    /// where there is one, explaining it covers the cause without being told to.</summary>
    private static string FromOutput()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var text = IdeOutputService.Instance.GetActivePaneText(OutputTailLines);
        return string.IsNullOrWhiteSpace(text)
            ? null
            : $"Explain this output.\n\n```\n{text}\n```";
    }
}

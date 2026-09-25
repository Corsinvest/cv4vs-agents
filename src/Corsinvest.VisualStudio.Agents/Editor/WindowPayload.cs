/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary><para>What the Error List and the Output window hand to a chat pane: the text a
/// prompt from their menu is about, or what "Add to chat" adds. Null when there is nothing.</para>
/// <para>
/// Unlike the editor prompts, the text is carried in the prompt itself: neither window is part of
/// the IDE context a chat pane sends (that is editor documents only), so a bare instruction would
/// reach the agent with nothing to explain.
/// </para></summary>
internal static class WindowPayload
{
    /// <summary>Enough of a build log to hold the error and the lines that led to it, when the
    /// user has selected nothing. Long enough for an MSBuild failure to be in it, short enough
    /// not to spend a turn's context on a pane that has been running all day.</summary>
    private const int OutputTailLines = 80;

    /// <summary>No fence: the rows are already one structured line each, and a fence would turn
    /// them into an opaque block.</summary>
    public static string FromErrorList()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var text = IdeErrorListService.Instance.GetSelectedText();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Fenced, unlike the Error List: a log is full of dashes and asterisks that markdown
    /// would eat.</summary>
    public static string FromOutput()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var text = IdeOutputService.Instance.GetActivePaneText(OutputTailLines);
        return string.IsNullOrWhiteSpace(text) ? null : $"```\n{text}\n```";
    }

    public static string For(PromptScope scope)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return scope switch
        {
            PromptScope.ErrorList => FromErrorList(),
            PromptScope.Output => FromOutput(),
            _ => null,
        };
    }
}

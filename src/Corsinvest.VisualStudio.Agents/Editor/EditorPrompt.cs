/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Editor;

/// <summary>Which context menu a prompt belongs to. The names are the keys in prompts.json:
/// renaming one loses that menu's prompts.</summary>
public enum PromptScope
{
    Editor,
    ErrorList,
    Output,
}

// Public, like Profile and for the same reason: the Options page that edits these is public.
public sealed class EditorPrompt
{
    public string Title { get; set; }

    /// <summary>The instruction alone — no code in it: the selection reaches the CLI on its own,
    /// through the IDE context the pane already sends.</summary>
    public string Prompt { get; set; }

    /// <summary>Greyed out without a selection, the way Copilot greys "Optimize selection". Editor
    /// only: the Error List always needs rows, and the Output falls back to its tail.</summary>
    public bool RequiresSelection { get; set; }

    /// <summary>Sends the turn on click instead of leaving the text in the composer. Off for a
    /// prompt written as an opening line the user means to finish typing first.</summary>
    public bool SendImmediately { get; set; }

    public EditorPrompt Clone() => (EditorPrompt)MemberwiseClone();
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Editor;

internal sealed class EditorPrompt
{
    public string Title { get; set; }

    /// <summary>The instruction alone — no code in it: the selection reaches the CLI on its own,
    /// through the IDE context the pane already sends.</summary>
    public string Prompt { get; set; }

    /// <summary>Greyed out without a selection, the way Copilot greys "Optimize selection".</summary>
    public bool RequiresSelection { get; set; }
}

/// <summary>The context-menu prompts. Hard-coded for now; an editor for them comes later, and
/// <see cref="Options.AgentsProfilesPage"/> is the precedent for a list-shaped Options page.</summary>
internal static class EditorPrompts
{
    public static IReadOnlyList<EditorPrompt> Items { get; } =
    [
        new EditorPrompt
        {
            Title = "Explain",
            Prompt = "Explain what this code does.",
        },
        new EditorPrompt
        {
            Title = "Review",
            Prompt = "Review this code and point out what you would change, and why.",
        },
        new EditorPrompt
        {
            Title = "Find bugs",
            Prompt = "Look for bugs in this code. Say so plainly if you find none.",
        },
        new EditorPrompt
        {
            Title = "Write tests",
            Prompt = "Write tests for this code, following the ones already in this project.",
        },
        new EditorPrompt
        {
            Title = "Simplify",
            Prompt = "Simplify this code without changing what it does.",
            RequiresSelection = true,
        },
    ];
}

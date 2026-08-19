/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Editor;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Custom Options page hosting the editor context menu's prompts. Like the profiles page,
/// these live in a plain JSON file (<see cref="EditorPromptStore"/>) rather than the VS settings
/// store, so the menu can read them without this page ever being opened.</summary>
[ComVisible(true)]
public class AgentsEditorPromptsPage : UIElementDialogPage
{
    private List<EditorPrompt> _prompts;

    public List<EditorPrompt> Prompts
    {
        get => _prompts ??= [.. EditorPromptStore.Load()];
        set => _prompts = value ?? [];
    }

    // Rebuilt each time the page is shown. Reload from disk here so the editor always reflects
    // the current file (it can be edited by hand between visits).
    protected override UIElement Child
    {
        get
        {
            _prompts = [.. EditorPromptStore.Load()];
            return new EditorPromptsControl(this);
        }
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        if (e.ApplyBehavior == ApplyKind.Apply && _prompts != null)
        {
            // Blocked rather than silently dropped: a row with no title renders as an empty menu
            // item, and one with no prompt sends nothing.
            if (Validate(_prompts, out var error))
            {
                EditorPromptStore.Save(_prompts);
            }
            else
            {
                ShellHelpers.ShowMessage(error, "Editor prompts", warning: true);
                e.ApplyBehavior = ApplyKind.Cancel;
                return;
            }
        }
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply) { AgentsOptions.RaiseApplied(); }
    }

    private static bool Validate(List<EditorPrompt> prompts, out string error)
    {
        if (prompts.Any(p => string.IsNullOrWhiteSpace(p.Title)))
        {
            error = "A prompt has no title. Give every prompt a title before saving.";
            return false;
        }
        if (prompts.Any(p => string.IsNullOrWhiteSpace(p.Prompt)))
        {
            error = "A prompt has no text. Write what to ask, or remove the row.";
            return false;
        }
        error = null;
        return true;
    }
}

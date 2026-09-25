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
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Custom Options page hosting the context menus' prompts, one tab per menu. Like the
/// profiles page, these live in a plain JSON file (<see cref="EditorPromptStore"/>) rather than the
/// VS settings store, so the menus can read them without this page ever being opened.</summary>
[ComVisible(true)]
public class AgentsEditorPromptsPage : UIElementDialogPage
{
    /// <summary>One tab per menu, in the order they appear. The text says what the prompt is
    /// handed in that menu, which is what differs between them.</summary>
    private static readonly (PromptScope Scope, string Header, string Description)[] Tabs =
    [
        (PromptScope.Editor, "Editor",
            "Prompts offered when you right-click code, under \"cv4vs Agents\". Picking one hands it to a chat pane — the file and selection are sent with it, so the prompt is the instruction alone. \"Send on click\" runs it straight away; clear it for a prompt you mean to finish typing first. Rows appear in the menu in this order."),
        (PromptScope.ErrorList, "Error List",
            "Prompts offered when you right-click the Error List, under \"cv4vs Agents\". The rows you selected are added below the prompt, so write the instruction alone. \"Send on click\" runs it straight away. Rows appear in the menu in this order."),
        (PromptScope.Output, "Output",
            "Prompts offered when you right-click the Output window, under \"cv4vs Agents\". What you selected in the pane — or, with nothing selected, its last 80 lines — is added below the prompt, so write the instruction alone. \"Send on click\" runs it straight away. Rows appear in the menu in this order."),
    ];

    private Dictionary<PromptScope, List<EditorPrompt>> _prompts;

    public Dictionary<PromptScope, List<EditorPrompt>> Prompts
    {
        get => _prompts ??= EditorPromptStore.Load();
        set => _prompts = value ?? new();
    }

    // Rebuilt each time the page is shown. Reload from disk here so the editor always reflects
    // the current file (it can be edited by hand between visits).
    protected override UIElement Child
    {
        get
        {
            _prompts = EditorPromptStore.Load();
            var tabs = new TabControl();
            foreach (var (scope, header, description) in Tabs)
            {
                tabs.Items.Add(new TabItem
                {
                    Header = header,
                    Content = new PromptListControl(this, scope, description, showNeedsSelection: scope == PromptScope.Editor),
                });
            }
            return tabs;
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
                ShellHelpers.ShowMessage(error, "Prompts", warning: true);
                e.ApplyBehavior = ApplyKind.Cancel;
                return;
            }
        }
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply) { AgentsOptions.RaiseApplied(); }
    }

    /// <summary>Names the tab the bad row is in: with three lists, "a prompt has no title" alone
    /// leaves the user hunting.</summary>
    private static bool Validate(Dictionary<PromptScope, List<EditorPrompt>> prompts, out string error)
    {
        foreach (var (scope, header, _) in Tabs)
        {
            if (!prompts.TryGetValue(scope, out var list)) { continue; }
            if (list.Any(p => string.IsNullOrWhiteSpace(p.Title)))
            {
                error = $"A prompt in the {header} tab has no title. Give every prompt a title before saving.";
                return false;
            }
            if (list.Any(p => string.IsNullOrWhiteSpace(p.Prompt)))
            {
                error = $"A prompt in the {header} tab has no text. Write what to ask, or remove the row.";
                return false;
            }
        }
        error = null;
        return true;
    }
}

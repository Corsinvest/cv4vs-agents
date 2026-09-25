/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Editor;
using Corsinvest.VisualStudio.Agents.Helpers;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Editor for one context menu's prompts, a tab of <see cref="AgentsEditorPromptsPage"/>.
/// Edits an ObservableCollection and writes it back to the page's list for its scope on every
/// change, so Apply finds the current state — the page owns persistence, this owns the UI. One
/// control for every menu: what differs is passed in.</summary>
public partial class PromptListControl : UserControl
{
    private readonly AgentsEditorPromptsPage _page;
    private readonly PromptScope _scope;
    private readonly ObservableCollection<EditorPrompt> _items;

    /// <param name="showNeedsSelection">The editor's alone: the other menus always have something
    /// to send or none at all, and a column that does nothing reads as broken.</param>
    public PromptListControl(AgentsEditorPromptsPage page, PromptScope scope, string description, bool showNeedsSelection)
    {
        InitializeComponent();
        _page = page;
        _scope = scope;
        Description.Text = description;
        NeedsSelectionColumn.Visibility = showNeedsSelection ? Visibility.Visible : Visibility.Collapsed;
        _items = [.. page.Prompts[scope]];
        _items.CollectionChanged += (_, __) => Commit();
        PromptsGrid.ItemsSource = _items;
    }

    /// <summary>Push the edited list back to the page. The grid edits the EditorPrompt objects in
    /// place, so this only has to keep the page's list in the same order as the grid.</summary>
    private void Commit() => _page.Prompts[_scope] = [.. _items];

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var added = new EditorPrompt { Title = "New prompt", Prompt = "" };
        _items.Add(added);
        PromptsGrid.SelectedItem = added;
        PromptsGrid.ScrollIntoView(added);
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (PromptsGrid.SelectedItem is EditorPrompt sel) { _items.Remove(sel); }
    }

    /// <summary>This menu's list only: the other tabs keep what they hold.</summary>
    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (!ShellHelpers.ConfirmOkCancel(
                "Replace this menu's prompts with the ones the extension ships with? Your own are lost.",
                "Prompts"))
        {
            return;
        }
        _items.Clear();
        foreach (var p in EditorPromptStore.Defaults(_scope)) { _items.Add(p); }
    }

    private void OnMoveUpClick(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDownClick(object sender, RoutedEventArgs e) => Move(+1);

    /// <summary>Reorder the selected row. The list order IS the menu order, so this is how the
    /// prompt you reach for most gets to the top.</summary>
    private void Move(int delta)
    {
        if (PromptsGrid.SelectedItem is not EditorPrompt sel) { return; }
        var from = _items.IndexOf(sel);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _items.Count) { return; }
        _items.Move(from, to);
        PromptsGrid.SelectedItem = sel;
    }
}

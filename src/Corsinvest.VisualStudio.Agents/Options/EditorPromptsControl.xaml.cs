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

/// <summary>Editor for the context menu's prompts, hosted by <see cref="AgentsEditorPromptsPage"/>.
/// Edits an ObservableCollection and writes it back to the page's list on every change, so Apply
/// finds the current state — the page owns persistence, this owns the UI.</summary>
public partial class EditorPromptsControl : UserControl
{
    private readonly AgentsEditorPromptsPage _page;
    private readonly ObservableCollection<EditorPrompt> _items;

    public EditorPromptsControl(AgentsEditorPromptsPage page)
    {
        InitializeComponent();
        _page = page;
        _items = [.. page.Prompts];
        _items.CollectionChanged += (_, __) => Commit();
        PromptsGrid.ItemsSource = _items;
    }

    /// <summary>Push the edited list back to the page. The grid edits the EditorPrompt objects in
    /// place, so this only has to keep the page's list in the same order as the grid.</summary>
    private void Commit() => _page.Prompts = [.. _items];

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

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        if (!ShellHelpers.ConfirmOkCancel(
                "Replace the list with the prompts the extension ships with? Your own are lost.",
                "Editor prompts"))
        {
            return;
        }
        _items.Clear();
        foreach (var p in EditorPromptStore.Defaults) { _items.Add(p); }
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

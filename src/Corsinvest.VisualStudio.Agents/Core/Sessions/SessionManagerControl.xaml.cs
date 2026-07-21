/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

/// <summary>
/// Reusable session browser: lists JSONL sessions for a workdir with search,
/// click-to-select, and hover rename/delete. Shared by the toolbar history
/// picker and (future) Manage Sessions dialog / chat session switcher.
/// </summary>
public partial class SessionManagerControl : UserControl
{
    /// <summary>Fires when the user clicks a session row. Argument is the
    /// session id, or <c>null</c> for the pinned "New session" entry.</summary>
    public event Action<string> SessionSelected;

    /// <summary>Raised when the user dismisses the picker with Esc. The popup's light-dismiss
    /// only covers clicking away — with focus inside the search box Esc reaches this control,
    /// not the popup.</summary>
    public event Action Cancelled;

    private readonly ClaudePaths _paths;
    private readonly string _workingDirectory;
    private readonly string _activeSessionId;
    private readonly ObservableCollection<SessionRow> _allSessions = [];
    private readonly ObservableCollection<SessionRow> _filtered = [];

    /// <summary>Created in code (not from XAML) so its inputs are required and immutable:
    /// <paramref name="paths"/> is the pane's profile config-dir, <paramref name="workingDirectory"/>
    /// its workdir, <paramref name="activeSessionId"/> the session to mark with a ✓ (null = none).
    /// A constructor (vs. setters) makes them mandatory and rules out the ordering trap that showed
    /// sessions as empty. The disk read is deferred to Loaded — no I/O until the popup is shown.</summary>
    public SessionManagerControl(ClaudePaths paths, string workingDirectory, string activeSessionId)
    {
        InitializeComponent();
        SessionsList.ItemsSource = _filtered;
        _paths = paths;
        _workingDirectory = workingDirectory;
        _activeSessionId = activeSessionId;
        Loaded += (_, _) => Refresh();
    }

    private void ApplyActiveFlag()
    {
        foreach (var r in _allSessions)
        {
            r.IsActive = !string.IsNullOrEmpty(_activeSessionId)
                         && r.Id == _activeSessionId;
        }
    }

    /// <summary>Reload the session list from disk. Cheap (~50 ms), safe to
    /// call on popup open or after a rename/delete.</summary>
    public void Refresh()
    {
        _allSessions.Clear();
        if (string.IsNullOrEmpty(_workingDirectory) || !System.IO.Directory.Exists(_workingDirectory))
        {
            ApplyFilter();
            return;
        }
        try
        {
            foreach (var s in new SessionManager(_paths, _workingDirectory).Load())
            {
                _allSessions.Add(SessionRow.From(s));
            }
        }
        catch
        {
            OutputWindowLogger.Warn("[sessions] failed to load the session list — picker may be empty");
            // Silent: an unreadable folder shouldn't crash the picker.
        }
        ApplyActiveFlag();
        ApplyFilter();
        // Focus is the host's job: FocusSearch must run after the popup is up.
    }

    private void ApplyFilter()
    {
        var query = (SearchBox.Text ?? string.Empty).Trim();
        _filtered.Clear();
        IEnumerable<SessionRow> source = _allSessions;
        if (!string.IsNullOrEmpty(query))
        {
            source = _allSessions.Where(r =>
                r.DisplayTitle?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        foreach (var r in source)
        {
            _filtered.Add(r);
        }
        EmptyHint.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>Esc dismisses the picker. Handled while tunneling: the TextBox (and the rename
    /// edit box) swallow Escape before it ever bubbles up as KeyDown. The rename box is the one
    /// exception — there Esc reverts the edit, so it's left alone.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _allSessions.Any(r => r.IsEditing)) { return; }
        Cancelled?.Invoke();
        e.Handled = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            // Move focus into the list and select the first item for Up/Down nav.
            if (_filtered.Count > 0)
            {
                SessionsList.SelectedIndex = 0;
                var container = SessionsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                container?.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _filtered.Count > 0)
        {
            // Pick the first filtered session — fastest selection path.
            SessionSelected?.Invoke(_filtered[0].Id);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SessionsList.SelectedItem is SessionRow row)
        {
            SessionSelected?.Invoke(row.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && SessionsList.SelectedIndex == 0)
        {
            // Bounce back to search: the user cycled past the top.
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            e.Handled = true;
        }
    }

    /// <summary>Focus the search box. Call AFTER the popup is laid out —
    /// Focus() on a non-visible element silently fails.</summary>
    public void FocusSearch()
    {
        // Render priority: the popup is up and the TextBox visible by then.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }));
    }

    private void OnRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) { return; }
        if (sender is FrameworkElement el)
        {
            // Session rows bind their session id (string) to Tag.
            SessionSelected?.Invoke(el.Tag as string);
        }
    }

    private void OnRename_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement el || el.Tag is not SessionRow row) { return; }
        BeginEdit(row);
    }

    private void BeginEdit(SessionRow row)
    {
        // Only one inline editor at a time: cancel any other editing row.
        foreach (var r in _allSessions)
        {
            if (r != row && r.IsEditing) { r.CancelEdit(); }
        }
        row.BeginEdit();

        // Loaded priority: late enough that the virtualized container is
        // realized, so ContainerFromItem returns non-null.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (SessionsList.ItemContainerGenerator.ContainerFromItem(row) is not ListBoxItem container) { return; }
            var tb = FindDescendant<TextBox>(container, "EditBox");
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }));
    }

    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not SessionRow row) { return; }
        if (e.Key == Key.Enter)
        {
            CommitEdit(row, tb.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not SessionRow row) { return; }
        if (!row.IsEditing) { return; }
        // Blur = commit. Matches the inline-rename UX of VS Solution Explorer.
        CommitEdit(row, tb.Text);
    }

    private void CommitEdit(SessionRow row, string newTitle)
    {
        var trimmed = (newTitle ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == row.DisplayTitle)
        {
            row.CancelEdit();
            return;
        }
        try
        {
            new SessionManager(_paths, _workingDirectory).Rename(row.Id, trimmed);
            row.DisplayTitle = trimmed;
            row.CancelEdit();
        }
        catch (Exception ex)
        {
            row.CancelEdit();
            MessageBox.Show(
                $"Failed to rename session:\n{ex.Message}",
                "Rename failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static T FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root == null) { return null; }
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) { return t; }
            var deeper = FindDescendant<T>(child, name);
            if (deeper != null) { return deeper; }
        }
        return null;
    }

    private void OnDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement el || el.Tag is not SessionRow row) { return; }

        // Always confirm: delete unlinks the JSONL from disk, no undo.
        var preview = string.IsNullOrEmpty(row.DisplayTitle) ? row.Id : row.DisplayTitle;
        var ok = MessageBox.Show(
            $"Delete session \"{preview}\"?\nThis cannot be undone.",
            "Delete session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (ok != MessageBoxResult.Yes) { return; }

        try
        {
            new SessionManager(_paths, _workingDirectory).Delete(row.Id);
            _allSessions.Remove(_allSessions.FirstOrDefault(r => r.Id == row.Id));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to delete session:\n{ex.Message}",
                "Delete failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}

/// <summary>
/// Display projection of <see cref="SessionInfo"/> for the row template.
/// Pre-computes title/time-ago/tooltip (no XAML converters needed) and is
/// INPC so inline rename updates the row without a Refresh().
/// </summary>
public sealed class SessionRow : INotifyPropertyChanged
{
    public string Id { get; set; }
    public string TimeAgoText { get; set; }
    public string TooltipText { get; set; }

    private string _displayTitle;
    public string DisplayTitle
    {
        get => _displayTitle;
        set { if (_displayTitle != value) { _displayTitle = value; OnPropertyChanged(); } }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        private set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(); } }
    }

    private bool _isActive;
    /// <summary>Marks this row as the host's "currently active" entry,
    /// rendered via a ✓ in XAML. No behavior attached.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; OnPropertyChanged(); } }
    }

    private string _editBuffer;
    /// <summary>Two-way bound to the inline TextBox while in edit mode.</summary>
    public string EditBuffer
    {
        get => _editBuffer;
        set { if (_editBuffer != value) { _editBuffer = value; OnPropertyChanged(); } }
    }

    public void BeginEdit()
    {
        EditBuffer = DisplayTitle ?? string.Empty;
        IsEditing = true;
    }

    public void CancelEdit() => IsEditing = false;

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static SessionRow From(SessionInfo s)
    {
        var title = string.IsNullOrEmpty(s.Title)
            ? s.Id.Substring(0, Math.Min(8, s.Id.Length))
            : s.Title;
        return new SessionRow
        {
            Id = s.Id,
            DisplayTitle = title,
            TimeAgoText = TimeAgo(s.LastUsedAt),
            TooltipText = $"{s.Id}\n{s.LastUsedAt:G}",
        };
    }

    private static string TimeAgo(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 60) { return "just now"; }
        if (span.TotalMinutes < 60) { return $"{(int)span.TotalMinutes}m"; }
        if (span.TotalHours < 24) { return $"{(int)span.TotalHours}h"; }
        return span.TotalDays < 30 ? $"{(int)span.TotalDays}d" : when.ToString("yyyy-MM-dd");
    }
}

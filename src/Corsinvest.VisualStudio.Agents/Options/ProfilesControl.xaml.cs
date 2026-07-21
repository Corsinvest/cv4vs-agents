/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Master-detail editor for environment profiles, hosted by
/// <see cref="AgentsProfilesPage"/>. Every mutation (add/remove profile,
/// field edit, env grid edit, paste-from-JSON) is written back into
/// <c>page.Profiles</c> immediately so <c>OnApply</c> persists the current state
/// without needing an explicit "save" step in this control.</summary>
public partial class ProfilesControl : UserControl
{
    private readonly AgentsProfilesPage _page;
    private readonly ObservableCollection<Profile> _profiles;
    private readonly ObservableCollection<EnvRow> _envRows = [];

    // Guards re-entrancy while the detail panel is being populated from a
    // freshly-selected profile: TextChanged/CellEditEnding handlers must not
    // write that same data back as if the user had typed it.
    private bool _loadingDetail;
    private Profile _selected;

    public ProfilesControl(AgentsProfilesPage page)
    {
        InitializeComponent();
        _page = page ?? throw new ArgumentNullException(nameof(page));

        _profiles = new ObservableCollection<Profile>(page.Profiles ?? []);
        ProfilesList.ItemsSource = _profiles;
        EnvGrid.ItemsSource = _envRows;

        if (_profiles.Count > 0) { ProfilesList.SelectedIndex = 0; }
    }

    // Pushes the current in-memory list into page.Profiles so OnApply serializes it.
    private void PersistProfiles() => _page.Profiles = [.. _profiles];

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ProfilesList.SelectedItem as Profile;
        LoadDetail(_selected);
    }

    private void LoadDetail(Profile profile)
    {
        _loadingDetail = true;
        try
        {
            DetailPanel.IsEnabled = profile != null;

            NameBox.Text = profile?.Name ?? "";
            DescriptionBox.Text = profile?.Description ?? "";

            _envRows.Clear();
            if (profile != null)
            {
                foreach (var kv in profile.Env)
                {
                    _envRows.Add(new EnvRow { Key = kv.Key, Value = kv.Value });
                }
            }
        }
        finally
        {
            _loadingDetail = false;
        }
        ValidateName();
    }

    private void OnAddProfileClick(object sender, RoutedEventArgs e)
    {
        var profile = new Profile
        {
            Name = "New profile",
            Enabled = true,
            Env = new Dictionary<string, string>
            {
                { "ANTHROPIC_BASE_URL", "" },
                { "ANTHROPIC_AUTH_TOKEN", "" },
                { "ANTHROPIC_MODEL", "" },
                { "ANTHROPIC_DEFAULT_SONNET_MODEL", "" },
                { "ANTHROPIC_DEFAULT_OPUS_MODEL", "" },
                { "ANTHROPIC_DEFAULT_HAIKU_MODEL", "" },
            },
        };
        _profiles.Add(profile);
        PersistProfiles();
        ProfilesList.SelectedItem = profile;
    }

    private void OnRemoveProfileClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { return; }
        var index = _profiles.IndexOf(_selected);
        _profiles.Remove(_selected);
        PersistProfiles();
        if (_profiles.Count > 0)
        {
            ProfilesList.SelectedIndex = Math.Min(index, _profiles.Count - 1);
        }
    }

    private void OnMoveProfileUpClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(-1);

    private void OnMoveProfileDownClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(+1);

    // Reorder within the saved list; the menu lists profiles in this order.
    private void MoveSelectedProfile(int delta)
    {
        if (_selected == null) { return; }
        var from = _profiles.IndexOf(_selected);
        var to = from + delta;
        if (to < 0 || to >= _profiles.Count) { return; }
        _profiles.Move(from, to);
        PersistProfiles();
        ProfilesList.SelectedItem = _selected;
    }

    private void OnAddEnvRowClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { return; }
        var row = new EnvRow { Key = "", Value = "" };
        _envRows.Add(row);
        EnvGrid.SelectedItem = row;
        EnvGrid.ScrollIntoView(row);
    }

    private void OnRemoveEnvRowClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null || EnvGrid.SelectedItem is not EnvRow row) { return; }
        var index = _envRows.IndexOf(row);
        _envRows.Remove(row);
        ApplyEnvRowsToProfile();
        // Keep focus on the grid by selecting a neighbouring row (previous, or first).
        if (_envRows.Count > 0)
        {
            EnvGrid.SelectedIndex = Math.Min(index, _envRows.Count - 1);
            EnvGrid.Focus();
        }
    }

    private void OnProfileEnabledClick(object sender, RoutedEventArgs e)
    {
        // The list checkbox is TwoWay-bound directly to Profile.Enabled, so the value
        // is already updated by the time this fires — just persist it.
        PersistProfiles();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDetail || _selected == null) { return; }
        _selected.Name = NameBox.Text ?? "";
        // Refresh the list item's Name display.
        ProfilesList.Items.Refresh();
        ValidateName();
        PersistProfiles();
    }

    private void OnDescriptionChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingDetail || _selected == null) { return; }
        _selected.Description = DescriptionBox.Text ?? "";
        PersistProfiles();
    }

    /// <summary>Name must be non-blank and unique (OrdinalIgnoreCase) among the
    /// other profiles. Known limitation (phase 1): this only flags the problem
    /// inline (red border/text) — it does not block Apply. UIElementDialogPage.OnApply
    /// has no supported way to cancel the Apply/close, so a hard block would need a
    /// bigger change (e.g. a custom modal dialog) left for a later phase.</summary>
    private void ValidateName()
    {
        if (_selected == null)
        {
            NameError.Visibility = Visibility.Collapsed;
            return;
        }
        var name = _selected.Name ?? "";
        var isDuplicate = _profiles.Any(p =>
            p != _selected && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var invalid = string.IsNullOrWhiteSpace(name) || isDuplicate;

        NameError.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
        NameBox.BorderBrush = invalid
            ? System.Windows.Media.Brushes.Red
            : SystemColors.ControlDarkBrush;
        NameBox.BorderThickness = invalid ? new Thickness(1.5) : new Thickness(1);
    }

    // The columns bind with UpdateSourceTrigger=PropertyChanged, so the edited EnvRow
    // is already current when CellEditEnding fires; RowEditEnding fires after the (new)
    // row is committed to the ItemsSource. Write back synchronously — deferring to a
    // Background dispatcher tick risked OnApply reading page.Profiles before the tick ran
    // (user clicks the Options OK button right after editing), silently dropping the last edit.
    private void OnEnvGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        => Dispatcher.BeginInvoke(new Action(ApplyEnvRowsToProfile), System.Windows.Threading.DispatcherPriority.Send);

    private void OnEnvGridRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        => ApplyEnvRowsToProfile();

    private void ApplyEnvRowsToProfile()
    {
        if (_loadingDetail || _selected == null) { return; }

        var env = new Dictionary<string, string>();
        foreach (var row in _envRows)
        {
            if (string.IsNullOrWhiteSpace(row.Key)) { continue; }
            env[row.Key] = row.Value ?? ""; // last value wins on duplicate key
        }
        _selected.Env = env;
        PersistProfiles();
    }

    private void OnEnvVarsLinkClick(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        // Open the CLI's env-vars doc in the default browser so the user can look up
        // which variables a profile can set.
        try { System.Diagnostics.Process.Start(e.Uri.AbsoluteUri); }
        catch (Exception) { /* no browser / blocked — ignore, not worth a dialog */ }
        e.Handled = true;
    }

    private void OnPasteJsonClick(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { return; }

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (Exception)
        {
            MessageBox.Show("Cannot read the clipboard.", "Paste from JSON",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        JObject parsed;
        try
        {
            var root = JObject.Parse(text);
            parsed = root["env"] as JObject ?? root;
        }
        catch (Exception)
        {
            MessageBox.Show("Invalid JSON", "Paste from JSON",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_envRows.Count > 0)
        {
            var ok = MessageBox.Show(
                "Replace the current environment variables with the pasted ones?",
                "Paste from JSON",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) { return; }
        }

        _envRows.Clear();
        foreach (var prop in parsed.Properties())
        {
            _envRows.Add(new EnvRow { Key = prop.Name, Value = prop.Value?.ToString() ?? "" });
        }
        ApplyEnvRowsToProfile();
    }
}

/// <summary>Editable key/value row bound to <see cref="ProfilesControl.EnvGrid"/>.
/// INPC so DataGrid cell edits are picked up without an explicit CommitEdit call.</summary>
public sealed class EnvRow : INotifyPropertyChanged
{
    private string _key;
    public string Key
    {
        get => _key;
        set { if (_key != value) { _key = value; OnPropertyChanged(); } }
    }

    private string _value;
    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

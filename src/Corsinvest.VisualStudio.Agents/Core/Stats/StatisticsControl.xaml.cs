/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Utils;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>The Statistics document-tab UI. Reads the aggregated usage from StatsService (the same
/// local .jsonl the WebView stats dialog uses) and fills the summary tiles + per-model table. No
/// cost column and no charts yet (both are later phases).</summary>
public partial class StatisticsControl : UserControl
{
    private readonly ClaudePaths _paths;
    private readonly string _workingDirectory;
    private bool _loaded;

    public StatisticsControl()
    {
        InitializeComponent();

        // The native "Claude" profile is the first one; stats read the .jsonl under its config dir.
        var profiles = ProfileStore.Load(forEdit: false);
        var profile = profiles.Count > 0 ? profiles[0] : null;
        _paths = profile != null ? ClaudePaths.ForProfile(profile) : null;
        _workingDirectory = AgentsPackage.Instance?.CurrentSolutionFolder ?? "";

        StatsService.IndexingCompleted += OnIndexingCompleted;
        Loaded += (_, _) =>
        {
            _loaded = true;
            _ = ReloadAsync();
        };
        Unloaded += (_, _) => StatsService.IndexingCompleted -= OnIndexingCompleted;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) { _ = ReloadAsync(); }
    }

    private void OnIndexingCompleted()
    {
        // Raised on a background thread — marshal to the UI before touching WPF.
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            await ReloadAsync();
        });
    }

    private async Task ReloadAsync()
    {
        if (_paths == null) { return; }
        try
        {
            var scope = SelectedScope();
            var range = SelectedRange();
            StatsService.StartIndexing(_workingDirectory, _paths);
            IndexingText.Visibility = StatsService.IsIndexing ? Visibility.Visible : Visibility.Collapsed;

            var wd = _workingDirectory;
            var paths = _paths;
            var resp = await Task.Run(() => StatsService.BuildResponse(scope, range, wd, "", paths));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Apply(resp);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatisticsControl.Reload", ex);
        }
    }

    private void Apply(StatsResponse r)
    {
        if (r == null) { return; }
        IndexingText.Visibility = r.Indexing ? Visibility.Visible : Visibility.Collapsed;
        TotalTokensText.Text = r.TotalTokens.ToString("N0");
        SessionsText.Text = r.TotalSessions.ToString("N0");
        MessagesText.Text = r.TotalMessages.ToString("N0");

        var toolCalls = 0;
        if (r.TopTools != null)
        {
            foreach (var t in r.TopTools) { toolCalls += t.Count; }
        }
        ToolCallsText.Text = toolCalls.ToString("N0");

        ModelGrid.ItemsSource = r.ModelBreakdown;
    }

    private StatsScope SelectedScope()
        => ScopeCombo.SelectedIndex == 1 ? StatsScope.Project : StatsScope.All;

    private StatsRange SelectedRange()
        => RangeCombo.SelectedIndex switch
        {
            0 => StatsRange.Last7d,
            2 => StatsRange.All,
            _ => StatsRange.Last30d,
        };
}

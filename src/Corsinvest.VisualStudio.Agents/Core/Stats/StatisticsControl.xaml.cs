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
            // Index once when the tab opens; when it finishes, OnIndexingCompleted re-reads.
            if (_paths != null)
            {
                var indexing = StatsService.StartIndexing(_workingDirectory, _paths);
                IndexingText.Visibility = indexing ? Visibility.Visible : Visibility.Collapsed;
            }
            _ = ReloadAsync();
        };
        Unloaded += (_, _) => StatsService.IndexingCompleted -= OnIndexingCompleted;
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only re-read from cache on a filter change — no re-index (that's a one-time open cost).
        if (_loaded) { _ = ReloadAsync(); }
    }

    private void OnIndexingCompleted()
    {
        // Raised on a background thread — marshal to the UI, hide the spinner, re-read once.
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IndexingText.Visibility = Visibility.Collapsed;
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
        TotalTokensText.Text = StatsFormat.FormatTokens(r.TotalTokens);
        TotalTokensText.ToolTip = r.TotalTokens.ToString("N0");

        long input = 0, output = 0, cache = 0;
        if (r.ModelBreakdown != null)
        {
            foreach (var m in r.ModelBreakdown)
            {
                input += m.InputTokens;
                output += m.OutputTokens;
                cache += m.CacheReadTokens + m.CacheCreationTokens;
            }
        }
        InputTokensText.Text = StatsFormat.FormatTokens(input);
        OutputTokensText.Text = StatsFormat.FormatTokens(output);
        CacheTokensText.Text = StatsFormat.FormatTokens(cache);

        SessionsText.Text = r.TotalSessions.ToString("N0");
        MessagesText.Text = r.TotalMessages.ToString("N0");

        var toolCalls = 0;
        if (r.TopTools != null)
        {
            foreach (var t in r.TopTools) { toolCalls += t.Count; }
        }
        ToolCallsText.Text = toolCalls.ToString("N0");

        ActiveDaysText.Text = r.ActiveDays.ToString("N0");
        CurrentStreakText.Text = r.CurrentStreak + "d";
        LongestStreakText.Text = r.LongestStreak + "d";
        PeakHourText.Text = r.PeakHour >= 0 ? $"{r.PeakHour:D2}:00" : "—";
        FavoriteModelText.Text = string.IsNullOrEmpty(r.FavoriteModel) ? "—" : r.FavoriteModel;
        ImagesText.Text = r.ImageCount.ToString("N0");
        AttachmentsText.Text = r.FileCount.ToString("N0");
        SubagentsText.Text = r.SubagentSessions.ToString("N0");
        SubagentTokensText.Text = StatsFormat.FormatTokens(r.SubagentTokens);

        var rows = new System.Collections.Generic.List<ModelRow>();
        if (r.ModelBreakdown != null)
        {
            for (var i = 0; i < r.ModelBreakdown.Length; i++)
            {
                rows.Add(ModelRow.From(r.ModelBreakdown[i], i));
            }
        }
        ModelsList.ItemsSource = rows;

        Heatmap.SetData(StatsChart.BuildHeatmap(r));
        BarChart.SetData(StatsChart.BuildBars(r));
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

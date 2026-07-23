/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Utils;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>The Statistics document-tab UI. A left tree (All → Profile → Project → Session) picks
/// the scope; each node recomputes the summary tiles + heatmap + per-model list/chart from the
/// StatsService cache (the same local .jsonl the WebView dialog uses). No cost column.</summary>
public partial class StatisticsControl : UserControl
{
    // The current tree selection (what BuildResponse aggregates). Starts on the current workspace's
    // project when there is one, else All.
    private StatsSelection _sel = new() { Scope = StatsScope.All };
    // The selected tree node (for the donut's child breakdown; _sel alone can't navigate children).
    private StatsTreeNode _selNode;
    private bool _loaded;
    // Monotonic reload token. A rebuild + re-select can start two ReloadAsyncs; each captures the
    // token at entry and only paints if it is still the latest, so a stale one can't overwrite the
    // fresh donut/tiles (the donut used to flash in then vanish).
    private int _reloadToken;

    public StatisticsControl()
    {
        InitializeComponent();

        _sel = InitialSelection();
        Loaded += (_, _) =>
        {
            // Loaded fires again every time the tab is re-activated / regains focus. Re-attach the
            // indexing event each time (Unloaded detaches it), but run the initial build + index ONLY
            // once — otherwise it would re-index on every tab switch.
            StatsService.IndexingCompleted -= OnIndexingCompleted;
            StatsService.IndexingCompleted += OnIndexingCompleted;
            if (_loaded) { return; }
            _loaded = true;
            BuildTree();
            SetIndexing(StatsService.StartIndexing());
            _ = ReloadAsync();
        };
        Unloaded += (_, _) => StatsService.IndexingCompleted -= OnIndexingCompleted;
    }

    // Open on the current workspace's project (the native profile's session folder for this
    // solution) when there is one; otherwise All. If the project isn't in the tree (no data yet /
    // out of range), BuildTree's FindPath falls back to All on its own.
    private static StatsSelection InitialSelection()
    {
        try
        {
            var wd = AgentsPackage.Instance?.CurrentSolutionFolder;
            if (string.IsNullOrEmpty(wd)) { return new StatsSelection { Scope = StatsScope.All }; }
            var profiles = ProfileStore.Load(forEdit: false);
            var profile = profiles.Count > 0 ? profiles[0] : null;
            if (profile == null) { return new StatsSelection { Scope = StatsScope.All }; }
            return new StatsSelection
            {
                Scope = StatsScope.Project,
                Profile = profile,
                ProjectDir = ClaudePaths.ForProfile(profile).SessionFolder(wd),
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatisticsControl.InitialSelection", ex);
            return new StatsSelection { Scope = StatsScope.All };
        }
    }

    // (Re)build the tree for the current range and re-select the matching node. If the previously
    // selected node no longer exists in range, fall back to All (root) and adopt its selection.
    private void BuildTree()
    {
        var root = StatsService.BuildTree(SelectedRange());

        var path = new List<StatsTreeNode>();
        if (!FindPath(root, _sel, path)) { path.Clear(); path.Add(root); }
        _selNode = path[path.Count - 1];
        _sel = _selNode.Selection;
        // Mark expand/select on the models BEFORE assigning ItemsSource, so the TreeViewItem
        // containers are generated already expanded (setting IsExpanded afterwards doesn't
        // retroactively generate the containers of a collapsed ancestor).
        for (var i = 0; i < path.Count - 1; i++) { path[i].IsExpanded = true; }
        path[path.Count - 1].IsSelected = true;

        ScopeTree.ItemsSource = new[] { root };

        // Give the tree keyboard focus and scroll the selected node into view, once the containers
        // exist (after this layout pass). Without focus the selection renders greyed-out/inactive.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ScopeTree.Focus();
            (ScopeTree.ItemContainerGenerator.ContainerFromItem(root) as TreeViewItem)?.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Depth-first search that also records the path (root … match). Returns false if no match.
    private static bool FindPath(StatsTreeNode node, StatsSelection sel, List<StatsTreeNode> path)
    {
        path.Add(node);
        if (Matches(node.Selection, sel)) { return true; }
        foreach (var c in node.Children)
        {
            if (FindPath(c, sel, path)) { return true; }
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static bool Matches(StatsSelection a, StatsSelection b)
        => a.Scope == b.Scope
        // Profiles are reloaded on every rebuild, so a new instance each time — compare by config-dir
        // (stable), not by reference, or the selection would never survive a range change.
        && string.Equals(ConfigIdOf(a.Profile), ConfigIdOf(b.Profile), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ProjectDir, b.ProjectDir, StringComparison.OrdinalIgnoreCase)
        // A folder node → first project dir; a day → its date; a session → its session id.
        && string.Equals(a.ProjectDirs?.FirstOrDefault(), b.ProjectDirs?.FirstOrDefault(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Date, b.Date, StringComparison.Ordinal)
        && string.Equals(a.SessionIds?.FirstOrDefault(), b.SessionIds?.FirstOrDefault(), StringComparison.Ordinal);

    private static string ConfigIdOf(Profile p) => p == null ? null : ClaudePaths.ForProfile(p).ConfigId;

    private void OnScopeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is StatsTreeNode node)
        {
            // BuildTree's programmatic IsSelected re-selects the very node it just stored in _selNode;
            // that echo would race the caller's own ReloadAsync (the donut flickered in then out). Only
            // a real user pick lands on a different node — reload just for those.
            if (ReferenceEquals(node, _selNode)) { return; }
            _selNode = node;
            _sel = node.Selection;
            if (_loaded) { _ = ReloadAsync(); }
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // A range change re-filters the tree (hiding out-of-range days/sessions) and re-reads. The
        // rebuild re-selects the current node if it survives, else falls back to All. No re-index.
        if (_loaded) { BuildTree(); _ = ReloadAsync(); }
    }

    // Refresh: incremental re-index (only changed files). Recreate: full re-index from scratch
    // (ignore cache — picks up moved files / changed cwd). Both show the spinner while running;
    // OnIndexingCompleted rebuilds the tree + reloads + re-enables. Single-flight: no-op if running.
    private void OnRefreshClick(object sender, RoutedEventArgs e) => StartIndex(force: false);

    private void OnRecreateClick(object sender, RoutedEventArgs e) => StartIndex(force: true);

    private void StartIndex(bool force)
    {
        if (StatsService.StartIndexing(force)) { SetIndexing(true); }
    }

    // While indexing: a centered progress bar replaces the content area (Overview + Models), and the
    // action buttons are disabled (not hidden); the range combo stays usable. Restored when done.
    private void SetIndexing(bool running)
    {
        LoadingPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ModelsPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RefreshButtons.IsEnabled = !running;
    }

    private void OnIndexingCompleted()
    {
        // Raised on a background thread — marshal to the UI, restore the buttons, rebuild the tree
        // (new sessions may have appeared) preserving the selection, then re-read.
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetIndexing(false);
            BuildTree();
            await ReloadAsync();
        });
    }

    private async Task ReloadAsync()
    {
        var token = ++_reloadToken;
        try
        {
            var range = SelectedRange();
            var sel = _sel;
            var node = _selNode;
            var resp = await Task.Run(() => StatsService.BuildResponse(sel, range));
            // The donut breaks the selected node's tokens down by child (empty for a leaf / <2 kids).
            var slices = await Task.Run(() => StatsService.ChildBreakdown(node, range));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            // A newer reload started while this one ran — drop this stale paint.
            if (token != _reloadToken) { return; }
            Apply(resp);
            Donut.SetData(slices);
            // Show even a single child (a full 100% ring) — a hidden donut where the node clearly has
            // data reads as a bug. Only a true leaf (no children) yields an empty list and hides it.
            var showDonut = slices.Count >= 1;
            BreakdownHead.Visibility = Donut.Visibility = showDonut ? Visibility.Visible : Visibility.Collapsed;
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

    private StatsRange SelectedRange()
        => RangeCombo.SelectedIndex switch
        {
            0 => StatsRange.Last7d,
            2 => StatsRange.All,
            _ => StatsRange.Last30d,
        };
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Core.Stats;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Utils;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>The Context usage document-tab UI. A left tree (All → Profile → Folder → Project →
/// Session, no calendar) picks a session; the right panel shows that session's context-window
/// breakdown, fetched with an ephemeral --resume CLI (ContextProbe). The tree is the Statistics tree
/// built calendar-free (includeDays: false); intermediate nodes only navigate (context is
/// per-session, not aggregable).</summary>
public partial class ContextUsageControl : UserControl
{
    // The current tree selection. Starts on the current workspace's project when there is one, else All.
    private StatsSelection _sel = new() { Scope = StatsScope.All };
    // The selected tree node (needed to read the session id / profile / project dir for the fetch).
    private StatsTreeNode _selNode;
    private bool _loaded;
    // Context of a closed session doesn't change → cache the first fetch; re-clicks are instant.
    private readonly Dictionary<string, GetContextUsageResponse> _cache = new();
    // Cancels the in-flight fetch when the selection changes.
    private CancellationTokenSource _cts;

    public ContextUsageControl()
    {
        InitializeComponent();

        _sel = InitialSelection();
        Loaded += (_, _) =>
        {
            // Loaded fires again on every tab re-activation. Re-attach the indexing event each time,
            // but run the initial build + index ONLY once (else it re-indexes on every tab switch).
            StatsService.IndexingCompleted -= OnIndexingCompleted;
            StatsService.IndexingCompleted += OnIndexingCompleted;
            if (_loaded) { return; }
            _loaded = true;
            BuildTree();
            SetIndexing(StatsService.StartIndexing());
            ShowSelectPrompt();
        };
        Unloaded += (_, _) => StatsService.IndexingCompleted -= OnIndexingCompleted;
    }

    // Open on the current workspace's project when there is one; otherwise All. If it isn't in the
    // tree, BuildTree's FindPath falls back to All on its own.
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
            OutputWindowLogger.LogException("ContextUsageControl.InitialSelection", ex);
            return new StatsSelection { Scope = StatsScope.All };
        }
    }

    // (Re)build the calendar-free tree for the current range and re-select the matching node. If the
    // previously selected node no longer exists in range, fall back to All (root).
    private void BuildTree()
    {
        var root = StatsService.BuildTree(SelectedRange(), includeDays: false);

        var path = new List<StatsTreeNode>();
        if (!FindPath(root, _sel, path)) { path.Clear(); path.Add(root); }
        _selNode = path[path.Count - 1];
        _sel = _selNode.Selection;
        for (var i = 0; i < path.Count - 1; i++) { path[i].IsExpanded = true; }
        path[path.Count - 1].IsSelected = true;

        ScopeTree.ItemsSource = new[] { root };

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
        && string.Equals(ConfigIdOf(a.Profile), ConfigIdOf(b.Profile), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ProjectDir, b.ProjectDir, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ProjectDirs?.FirstOrDefault(), b.ProjectDirs?.FirstOrDefault(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Date, b.Date, StringComparison.Ordinal)
        && string.Equals(a.SessionIds?.FirstOrDefault(), b.SessionIds?.FirstOrDefault(), StringComparison.Ordinal);

    private static string ConfigIdOf(Profile p) => p == null ? null : ClaudePaths.ForProfile(p).ConfigId;

    private void OnScopeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not StatsTreeNode node) { return; }
        // BuildTree's programmatic IsSelected re-selects the node it just stored — ignore that echo.
        if (ReferenceEquals(node, _selNode)) { return; }
        _selNode = node;
        _sel = node.Selection;
        if (!_loaded) { return; }
        // Context is per-session; intermediate nodes only navigate.
        if (node.Selection.Scope == StatsScope.Session) { OnSessionSelected(node); }
        else { ShowSelectPrompt(); }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // A range change re-filters the tree and re-selects the current node if it survives. No re-index.
        if (_loaded) { BuildTree(); }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => StartIndex(force: false);

    private void OnRecreateClick(object sender, RoutedEventArgs e) => StartIndex(force: true);

    private void StartIndex(bool force)
    {
        // A manual Refresh/Recreate should re-fetch the visible session (drop its cached context).
        var sid = _selNode?.Selection?.SessionIds?.FirstOrDefault();
        if (sid != null) { _cache.Remove(sid); }
        if (StatsService.StartIndexing(force)) { SetIndexing(true); }
    }

    // While indexing: the centered progress bar replaces the panel; the action buttons are disabled.
    private void SetIndexing(bool running)
    {
        LoadingPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ContextScroller.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RefreshButtons.IsEnabled = !running;
    }

    private void OnIndexingCompleted()
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetIndexing(false);
            BuildTree();
        });
    }

    // The panel prompt shown for intermediate nodes / before any session is picked.
    private void ShowSelectPrompt()
    {
        ContextPanel.Children.Clear();
        ContextPanel.Children.Add(new TextBlock
        {
            Text = "Select a session to see its context usage.",
            Opacity = 0.7,
            Margin = new Thickness(12),
        });
    }

    // The panel message when a fetch fails.
    private void ShowUnavailable()
    {
        ContextPanel.Children.Clear();
        ContextPanel.Children.Add(new TextBlock
        {
            Text = "Context usage unavailable.",
            Opacity = 0.7,
            Margin = new Thickness(12),
        });
    }

    // A session was picked: serve it from cache, else fetch it with an ephemeral --resume CLI.
    private void OnSessionSelected(StatsTreeNode node)
    {
        var sel = node.Selection;
        var sid = sel.SessionIds?.FirstOrDefault();
        if (string.IsNullOrEmpty(sid)) { ShowSelectPrompt(); return; }

        if (_cache.TryGetValue(sid, out var cached)) { SetFetching(false); RenderContext(cached); return; }

        // Cancel a previous in-flight fetch; start a fresh one.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetFetching(true);
        var profile = sel.Profile;
        var wd = StatsService.CwdForProject(profile, sel.ProjectDir);
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var data = await ContextProbe.FetchAsync(profile, wd, sid, ct);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (ct.IsCancellationRequested) { return; } // selection moved on
                SetFetching(false);
                if (data == null) { ShowUnavailable(); return; }
                _cache[sid] = data;
                RenderContext(data);
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("ContextUsageControl.Fetch", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!ct.IsCancellationRequested) { SetFetching(false); ShowUnavailable(); }
            }
        });
    }

    // While fetching: the centered progress bar replaces the panel; the action buttons are disabled.
    private void SetFetching(bool on)
    {
        LoadingPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ContextScroller.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RefreshButtons.IsEnabled = !on;
    }

    private StatsRange SelectedRange()
        => RangeCombo.SelectedIndex switch
        {
            0 => StatsRange.Last7d,
            2 => StatsRange.All,
            _ => StatsRange.Last30d,
        };
}

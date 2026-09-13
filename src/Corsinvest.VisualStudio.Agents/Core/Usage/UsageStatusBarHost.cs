/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>
/// Puts the usage item into Visual Studio's status bar, and takes it out again.
/// <para>VS offers no API for a custom status bar element, so this goes through the WPF tree: the main
/// window holds a single <see cref="StatusBar"/>, and an item docked right lands beside the notification
/// bell. That tree is VS's own and may change between releases; if the bar can't be found the item just
/// doesn't appear, the log says so, and nothing else is affected.</para>
/// <para>UI thread only.</para>
/// </summary>
internal static class UsageStatusBarHost
{
    // The package loads at shell start, possibly while the start window still stands in for the main
    // window's content — so the bar is looked for again, for about a minute, before giving up.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
    ];

    private static UsageStatusBarItem _control;
    private static StatusBarItem _item;
    private static CancellationTokenSource _attachCts;
    private static bool _initialized;

    /// <summary>Show the item if Options want it, and follow later changes to that option.</summary>
    public static void Initialize()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_initialized) { return; }
        _initialized = true;
        AgentsOptions.Applied += OnOptionsApplied;
        Sync();
    }

    /// <summary>Package teardown: remove the item and stop refreshing.</summary>
    public static void Shutdown()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!_initialized) { return; }
        _initialized = false;
        AgentsOptions.Applied -= OnOptionsApplied;
        Detach();
    }

    private static void OnOptionsApplied()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            Sync();
            UsageStatusService.Instance.OnOptionsApplied();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("[usage-status] OnOptionsApplied", ex);
        }
    }

    private static void Sync()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (AgentsOptions.General.ShowUsageInStatusBar) { Attach(); }
        else { Detach(); }
    }

    private static void Attach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        UsageStatusService.Instance.Start();
        if (_item != null || _attachCts != null) { return; }

        var cts = new CancellationTokenSource();
        _attachCts = cts;
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                foreach (var delay in RetryDelays)
                {
                    if (delay > TimeSpan.Zero) { await Task.Delay(delay, cts.Token); }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                    if (TryInsert())
                    {
                        _attachCts = null;
                        return;
                    }
                }
                _attachCts = null;
                OutputWindowLogger.Global.Warn("[usage-status] Visual Studio's status bar was not found — the usage item is not shown");
            }
            catch (OperationCanceledException)
            {
                // Detached, or VS is closing, while still looking.
            }
        }).FileAndForget(nameof(UsageStatusBarHost));
    }

    private static void Detach()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _attachCts?.Cancel();
        _attachCts = null;
        _control?.ClosePopup();
        RemoveItem();
        UsageStatusService.Instance.Stop();
    }

    private static bool TryInsert()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Application.Current?.MainWindow is not Window main || !main.IsLoaded) { return false; }
        if (FindDescendant<StatusBar>(main) is not StatusBar bar) { return false; }
        if (bar.ItemsSource != null)
        {
            // A bound bar takes no extra items: a layout this code doesn't know, not a timing problem.
            OutputWindowLogger.Global.Warn("[usage-status] the status bar is data-bound — the usage item is not shown");
            return true;
        }

        _control ??= new UsageStatusBarItem();
        var item = new StatusBarItem
        {
            Content = _control,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        DockPanel.SetDock(item, Dock.Right);
        bar.Items.Add(item);
        item.Unloaded += OnItemUnloaded;
        _item = item;
        OutputWindowLogger.Global.Info("[usage-status] usage item added to the status bar");
        return true;
    }

    // VS takes the bar down when it closes, and may rebuild it on a layout change — then the item belongs
    // in the new bar. The old one is gone either way, so start over.
    private static void OnItemUnloaded(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!ReferenceEquals(sender, _item)) { return; }
        RemoveItem();
        if (_initialized && AgentsOptions.General.ShowUsageInStatusBar) { Attach(); }
    }

    private static void RemoveItem()
    {
        if (_item == null) { return; }
        _item.Unloaded -= OnItemUnloaded;
        ItemsControl.ItemsControlFromItemContainer(_item)?.Items.Remove(_item);
        // The control is reused by the next item, and WPF allows it one logical parent at a time.
        _item.Content = null;
        _item = null;
    }

    // Breadth-first: the status bar sits a few levels under the window, while a depth-first walk would go
    // through the whole editor area first.
    private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is T match && !ReferenceEquals(node, root)) { return match; }
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) { queue.Enqueue(VisualTreeHelper.GetChild(node, i)); }
        }
        return null;
    }
}

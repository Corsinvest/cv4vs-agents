/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Tool window hosting the Statistics dashboard. Statistics owns no file: it renders what
/// StatsService aggregates, and there is nothing to save, rename or reopen from the recent list.
/// A document tab is the wrong shape for that — it needs a moniker, so the earlier version opened a
/// placeholder .cv4vsstats file just to have one, and then fought the shell over the tab caption.
/// Single-instance: reopening from the menu focuses this one.</summary>
[Guid("6f2e9a71-4c58-4b0e-9d3a-1e8b5c7a2d64")]
public sealed class StatisticsWindow : ToolWindowPane
{
    public StatisticsWindow() : base(null)
    {
        Caption = "Statistics";
        Content = new StatisticsControl();
    }

    /// <summary>Show the Statistics window, creating it on first use.</summary>
    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                // Single instance (id 0), created on demand.
                var window = pkg.FindToolWindow(typeof(StatisticsWindow), 0, create: true);
                if (window?.Frame is not Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame)
                {
                    OutputWindowLogger.Global.Warn("[stats] the Statistics window has no frame — cannot show it");
                    return;
                }
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("StatisticsWindow.Open", ex);
            }
        });
    }
}

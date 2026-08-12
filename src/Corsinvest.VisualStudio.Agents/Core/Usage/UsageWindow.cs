/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Tool window showing the plan's usage and rate limits, fetched live from the CLI. Owns no
/// file — see StatisticsWindow for why that makes a tool window the right shape. Single-instance.</summary>
[Guid("3d7c14e9-8b26-4a5f-9e01-7c4a8d2b6f35")]
public sealed class UsageWindow : ToolWindowPane
{
    public UsageWindow() : base(null)
    {
        Caption = "Usage";
        Content = new UsageControl();
    }

    /// <summary>Show the Usage window, creating it on first use.</summary>
    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = pkg.FindToolWindow(typeof(UsageWindow), 0, create: true);
                if (window?.Frame is not IVsWindowFrame frame)
                {
                    OutputWindowLogger.Global.Warn("[usage] the Usage window has no frame — cannot show it");
                    return;
                }
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("UsageWindow.Open", ex);
            }
        });
    }
}

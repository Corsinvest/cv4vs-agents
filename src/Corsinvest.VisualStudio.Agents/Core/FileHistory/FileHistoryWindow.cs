/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Core.FileHistory;

/// <summary>Tool window listing what the CLI's file backups occupy on disk, and deleting them.
/// Owns no file — see StatisticsWindow for why that makes a tool window the right shape.
/// Single-instance.</summary>
[Guid("9e6b3f24-71ad-4c85-b0f2-3d5817ea9c46")]
public sealed class FileHistoryWindow : ToolWindowPane
{
    public FileHistoryWindow() : base(null)
    {
        Caption = "File history";
        Content = new FileHistoryControl();
    }

    /// <summary>Show the File history window, creating it on first use.</summary>
    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = pkg.FindToolWindow(typeof(FileHistoryWindow), 0, create: true);
                if (window?.Frame is not IVsWindowFrame frame)
                {
                    OutputWindowLogger.Global.Warn("[filehistory] the File history window has no frame — cannot show it");
                    return;
                }
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("FileHistoryWindow.Open", ex);
            }
        });
    }
}

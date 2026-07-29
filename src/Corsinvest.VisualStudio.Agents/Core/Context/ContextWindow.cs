/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>Tool window showing a session's context breakdown, fetched with an ephemeral --resume
/// CLI. Owns no file — see StatisticsWindow for why that makes a tool window the right shape.
/// Single-instance.</summary>
[Guid("b1a45c38-6d92-4e7b-8f03-2a9e5d1c84b7")]
public sealed class ContextWindow : ToolWindowPane
{
    public ContextWindow() : base(null)
    {
        Caption = "Context usage";
        Content = new ContextUsageControl();
    }

    /// <summary>Show the Context usage window, creating it on first use.</summary>
    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = pkg.FindToolWindow(typeof(ContextWindow), 0, create: true);
                if (window?.Frame is not IVsWindowFrame frame)
                {
                    OutputWindowLogger.Warn("[context] the Context usage window has no frame — cannot show it");
                    return;
                }
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("ContextWindow.Open", ex);
            }
        });
    }
}

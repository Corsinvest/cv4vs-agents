/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// Read the VS Output window panes (Build, Debug, the running program's output, …) via the
/// public EnvDTE API. Useful when a command's output doesn't flow through the shell — e.g. the
/// debuggee's Console writes, or the build log. All on the UI thread; never throws.
/// </summary>
internal sealed class IdeOutputService
{
    public static IdeOutputService Instance { get; } = new();

    public sealed class OutputResult
    {
        public bool Ok { get; set; }
        public string Pane { get; set; }
        public string Content { get; set; }
        public int TotalLines { get; set; }
        public bool Truncated { get; set; }
        public string[] AvailablePanes { get; set; } = [];
        public string Reason { get; set; }
    }

    private static OutputWindow GetOutputWindow()
    {
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var win = dte?.Windows?.Item(Constants.vsWindowKindOutput);
        return win?.Object as OutputWindow;
    }

    /// <summary>Locate a pane by name (case-insensitive) and collect the sorted list of all
    /// pane names. Returns null when no match — the caller reports availablePanes so the model
    /// can retry. Must be called on the UI thread.</summary>
    private static OutputWindowPane FindPane(OutputWindow ow, string paneName, out string[] allPaneNames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var panes = new List<string>();
        OutputWindowPane match = null;
        foreach (OutputWindowPane p in ow.OutputWindowPanes)
        {
            panes.Add(p.Name);
            if (match == null && string.Equals(p.Name, paneName, StringComparison.OrdinalIgnoreCase)) { match = p; }
        }
        panes.Sort(StringComparer.OrdinalIgnoreCase);
        allPaneNames = [.. panes];
        return match;
    }

    /// <summary>Read a pane by name (case-insensitive). With no name, returns the list of
    /// available pane names instead of content. tailLines caps the returned lines.</summary>
    public async Task<OutputResult> ReadAsync(string paneName, int tailLines)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var ow = GetOutputWindow();
            if (ow == null) { return new OutputResult { Ok = false, Reason = "Output window not available." }; }

            var pane = FindPane(ow, paneName, out var panes);

            // No pane name → just list the panes the model can ask for.
            if (string.IsNullOrWhiteSpace(paneName))
            {
                return new OutputResult { Ok = true, AvailablePanes = panes };
            }

            if (pane == null)
            {
                return new OutputResult { Ok = false, AvailablePanes = panes, Reason = $"No output pane named '{paneName}'." };
            }

            // The pane text is reachable through its TextDocument: select all, read the text.
            var td = pane.TextDocument;
            var sel = td.Selection;
            sel.StartOfDocument(false);
            sel.EndOfDocument(true); // extend selection to the end
            var text = sel.Text ?? "";

            var allLines = text.Replace("\r\n", "\n").Split('\n');
            var total = allLines.Length;
            var truncated = tailLines > 0 && total > tailLines;
            var content = truncated
                ? string.Join("\n", allLines.Skip(total - tailLines))
                : text;

            return new OutputResult
            {
                Ok = true,
                Pane = pane.Name,
                Content = content,
                TotalLines = total,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeOutputService.ReadAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to read output window." };
        }
    }

    /// <summary>Clear a pane by name (case-insensitive). Used before an action so a later
    /// read returns only fresh output. No-op result (Ok=false) when the pane isn't found.</summary>
    public async Task<OutputResult> ClearAsync(string paneName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var ow = GetOutputWindow();
            if (ow == null) { return new OutputResult { Ok = false, Reason = "Output window not available." }; }

            var pane = FindPane(ow, paneName, out var panes);
            if (pane == null)
            {
                return new OutputResult { Ok = false, AvailablePanes = panes, Reason = $"No output pane named '{paneName}'." };
            }

            pane.Clear();
            return new OutputResult { Ok = true, Pane = pane.Name };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeOutputService.ClearAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to clear output pane." };
        }
    }

    /// <summary>Bring a pane to the foreground (case-insensitive) so the user sees it — used at
    /// debug checkpoints. Ok=false when the pane isn't found.</summary>
    public async Task<OutputResult> ActivateAsync(string paneName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var ow = GetOutputWindow();
            if (ow == null) { return new OutputResult { Ok = false, Reason = "Output window not available." }; }

            var pane = FindPane(ow, paneName, out var panes);
            if (pane == null)
            {
                return new OutputResult { Ok = false, AvailablePanes = panes, Reason = $"No output pane named '{paneName}'." };
            }

            pane.Activate();
            return new OutputResult { Ok = true, Pane = pane.Name };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeOutputService.ActivateAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to activate output pane." };
        }
    }
}

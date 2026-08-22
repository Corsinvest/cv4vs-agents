/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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

        /// <summary>Lines matching the pattern, or null when there was none. Without it a short
        /// answer reads as a short pane: 3 of 40000 lines and 3 lines total look the same.</summary>
        public int? MatchedLines { get; set; }
        public bool Truncated { get; set; }
        public string[] AvailablePanes { get; set; } = [];
        public string Reason { get; set; }
    }

    private static OutputWindow GetOutputWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var win = dte?.Windows?.Item(EnvDTE.Constants.vsWindowKindOutput);
        return win?.Object as OutputWindow;
    }

    /// <summary>The built-in panes, by the English name a caller would ask for. VS localises the
    /// displayed names — on an Italian IDE the Build pane is called "Compilazione" — so matching
    /// the name alone never finds them outside an English install. The GUIDs don't change.</summary>
    private static readonly Dictionary<string, Guid> WellKnownPanes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Build"] = VSConstants.OutputWindowPaneGuid.BuildOutputPane_guid,
        ["Debug"] = VSConstants.OutputWindowPaneGuid.DebugPane_guid,
        ["General"] = VSConstants.OutputWindowPaneGuid.GeneralPane_guid,
        ["Build Order"] = VSConstants.OutputWindowPaneGuid.SortedBuildOutputPane_guid,
    };

    /// <summary>Resolve a built-in pane by GUID. The DTE pane object carries its own Guid as a
    /// string, so the match is on identity and never on the localised display name.
    ///
    /// The IVsOutputWindow round-trip is only there for the pane that does not exist yet — VS
    /// creates them lazily, and CreatePane materialises one without bringing the window forward.
    /// Returns null for panes with no stable GUID; those are found by name. Must be called on the
    /// UI thread.</summary>
    private static OutputWindowPane FindWellKnownPane(OutputWindow ow, string paneName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!WellKnownPanes.TryGetValue(paneName, out var guid)) { return null; }

        var match = PaneByGuid(ow, guid);
        if (match != null) { return match; }

        // Not in the collection yet: ask the shell to create it, then look again.
        var vsOut = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (vsOut == null) { return null; }
        if (vsOut.CreatePane(ref guid, paneName, 1, 0) != VSConstants.S_OK) { return null; }
        return PaneByGuid(ow, guid);
    }

    /// <summary>The pane whose own Guid property matches, or null. Must be called on the UI
    /// thread.</summary>
    private static OutputWindowPane PaneByGuid(OutputWindow ow, Guid guid)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (OutputWindowPane p in ow.OutputWindowPanes)
        {
            try
            {
                if (Guid.TryParse(p.Guid, out var paneGuid) && paneGuid == guid) { return p; }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.Warn($"[ide] could not read the GUID of output pane '{p.Name}': {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Locate a pane by name (case-insensitive) and collect the sorted list of all
    /// pane names. Returns null when no match — the caller reports availablePanes so the model
    /// can retry. Must be called on the UI thread.</summary>
    private static OutputWindowPane FindPane(OutputWindow ow, string paneName, out string[] allPaneNames)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // By GUID first: the built-in panes carry a localised name, so asking for "Build" only
        // ever matches on an English IDE. This also creates the pane when it doesn't exist yet.
        var match = string.IsNullOrWhiteSpace(paneName) ? null : FindWellKnownPane(ow, paneName);

        var panes = new List<string>();
        foreach (OutputWindowPane p in ow.OutputWindowPanes)
        {
            panes.Add(p.Name);
            if (match == null && string.Equals(p.Name, paneName, StringComparison.OrdinalIgnoreCase)) { match = p; }
        }
        panes.Sort(StringComparer.OrdinalIgnoreCase);
        allPaneNames = [.. panes];
        return match;
    }

    /// <summary>Get the pane's TextDocument, the only way EnvDTE offers to read its text.
    ///
    /// A pane that has never been shown answers E_FAIL here: it is listed, and it holds the text
    /// the build wrote, but the document behind it is realised together with the window. Activating
    /// the pane realises it, so the failure is retried once that way rather than reported — the
    /// alternative is a caller that cannot tell "no output" from "output you haven't looked at".
    /// Activating brings the Output window forward, which is why it is done only on that branch.
    ///
    /// Returns null when even the retry fails. Must be called on the UI thread.</summary>
    private static TextDocument GetTextDocument(OutputWindowPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return pane.TextDocument;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Debug(() => $"[ide] pane '{pane.Name}' has no TextDocument yet ({ex.Message}); activating and retrying");
        }

        try
        {
            pane.Activate();
            return pane.TextDocument;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[ide] pane '{pane.Name}' still unreadable after activating: {ex.Message}");
            return null;
        }
    }

    /// <summary>Read a pane by name (case-insensitive). With no name, returns the list of
    /// available pane names instead of content. tailLines caps the returned lines; pattern keeps
    /// only the lines matching a regex.</summary>
    public async Task<OutputResult> ReadAsync(string paneName, int tailLines, string pattern)
    {
        System.Text.RegularExpressions.Regex rx = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            // Compiled before the thread switch: a bad pattern is the caller's mistake and should
            // come back as one, not as an exception thrown from the middle of a UI-thread read.
            try
            {
                rx = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                return new OutputResult { Ok = false, Reason = $"Invalid pattern: {ex.Message}" };
            }
        }

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
            var td = GetTextDocument(pane);
            if (td == null)
            {
                return new OutputResult
                {
                    Ok = false,
                    Pane = pane.Name,
                    AvailablePanes = panes,
                    Reason = $"The '{pane.Name}' pane exists but its text could not be read.",
                };
            }

            // Read through an EditPoint rather than by selecting all: selecting moves the caret and
            // highlight the user left in that pane, and a read during a fix→build→read loop would
            // take it away from them on every pass.
            var text = td.StartPoint.CreateEditPoint().GetText(td.EndPoint) ?? "";

            // A pane's text ends with the newline that closed its last line, so Split leaves an
            // empty element after it. Counted, it made totalLines one too many; tailed, it WAS the
            // answer — tailLines:1 returned "" instead of the last line anyone had written. An
            // empty pane is the same artefact at zero: "" splits to one empty element, and the
            // pane reported a line it did not have.
            var allLines = text.Length == 0
                ? []
                : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            var total = allLines.Length;

            // Filter first, then tail: "the last N lines that match" is what searching a long log
            // wants. Tailing first would answer "the matches among the last N", which finds nothing
            // whenever what is being looked for scrolled past.
            var lines = allLines;
            int? matched = null;
            if (rx != null)
            {
                lines = [.. allLines.Where(l => rx.IsMatch(l))];
                matched = lines.Length;
            }

            var truncated = tailLines > 0 && lines.Length > tailLines;
            var kept = truncated ? lines.Skip(lines.Length - tailLines) : lines;
            // Only re-join when something was dropped or filtered; otherwise the original text keeps
            // whatever line endings the pane wrote.
            var content = truncated || rx != null ? string.Join("\n", kept) : text;

            return new OutputResult
            {
                Ok = true,
                Pane = pane.Name,
                Content = content,
                TotalLines = total,
                MatchedLines = matched,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeOutputService.ReadAsync", ex);
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
            OutputWindowLogger.Global.LogException("IdeOutputService.ClearAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to clear output pane." };
        }
    }

    /// <summary>Write text to a pane, creating a custom one by that name if it doesn't exist —
    /// the built-in panes are owned by VS, but a caller writing progress or a note of its own
    /// wants a pane to appear rather than an error. Appends a newline unless the text ends in one,
    /// so consecutive writes don't run together.</summary>
    public async Task<OutputResult> WriteAsync(string paneName, string text, bool activate)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var ow = GetOutputWindow();
            if (ow == null) { return new OutputResult { Ok = false, Reason = "Output window not available." }; }

            var pane = FindPane(ow, paneName, out var panes)
                ?? ow.OutputWindowPanes.Add(paneName);
            if (pane == null)
            {
                return new OutputResult { Ok = false, AvailablePanes = panes, Reason = $"Could not create output pane '{paneName}'." };
            }

            pane.OutputString(text?.EndsWith("\n") == true ? text : text + "\n");
            if (activate) { pane.Activate(); }
            return new OutputResult { Ok = true, Pane = pane.Name };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeOutputService.WriteAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to write to the output pane." };
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
            OutputWindowLogger.Global.LogException("IdeOutputService.ActivateAsync", ex);
            return new OutputResult { Ok = false, Reason = "Failed to activate output pane." };
        }
    }
}

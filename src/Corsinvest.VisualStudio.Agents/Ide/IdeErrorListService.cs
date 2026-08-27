/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Reads the rows the user has selected in the Error List, via the public EnvDTE80 API.
/// Unlike the Output window this is not text but records — severity, file, line, project are
/// separate fields — so the caller gets them formatted rather than scraped.</summary>
internal sealed class IdeErrorListService
{
    public static IdeErrorListService Instance { get; } = new();

    /// <summary>Cap on how many rows go into one prompt: selecting the whole list of a broken
    /// build is one Ctrl+A, and the first rows are the ones worth asking about — the rest are
    /// usually knock-on errors.</summary>
    private const int MaxRows = 40;

    /// <summary><para>
    /// The selected rows, one per line, as "Error in Foo.cs:12 (MyProj): text". Null when
    /// nothing is selected — the entry greys out rather than explaining rows the user did not pick.
    /// </para>
    /// <para>
    /// UI thread; never throws — it runs from a menu query, where an exception would take the
    /// context menu down with it.
    /// </para></summary>
    public string GetSelectedText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var errorList = (Package.GetGlobalService(typeof(DTE)) as DTE2)?.ToolWindows?.ErrorList;
            if (errorList == null) { return null; }

            var selected = errorList.SelectedItems;
            var text = Format(selected as System.Collections.IEnumerable);
            if (text != null) { return text; }

            OutputWindowLogger.Global.Debug(
                () => $"[ide] Error List: DTE SelectedItems is {selected?.GetType().FullName ?? "null"}, asking the task list");
            return SelectedViaTaskList();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[ide] could not read the Error List selection: {ex.Message}");
            return null;
        }
    }

    /// <summary>One line per row, or null when there is nothing readable in there.</summary>
    private static string Format(System.Collections.IEnumerable items)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (items == null) { return null; }

        var lines = new List<string>();
        foreach (var item in items)
        {
            if (lines.Count >= MaxRows) { break; }
            // Per row, like GetDiagnosticsAsync: every ErrorItem member is a COM call that can
            // fail on a row the Error List is rebuilding underneath us, and half a line read out
            // of one that is going away is worth less than skipping it.
            try { if (item is ErrorItem err) { lines.Add(Format(err)); } }
            catch (Exception) { /* row went away mid-read */ }
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary><para>
    /// The selection straight from the shell's task list, for when DTE's SelectedItems
    /// answers with nothing. It is the window itself rather than the automation layer over it, so
    /// it reports what is highlighted even when SelectedItems does not.
    /// </para>
    /// <para>
    /// Text only: an IVsTaskItem carries its own columns and no ErrorItem behind it, so this loses
    /// the project name that the DTE path prints. The row's own text is what the user selected.
    /// </para></summary>
    private static string SelectedViaTaskList()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsErrorList)) is not IVsTaskList2 list) { return null; }
        if (list.EnumSelectedItems(out var items) != VSConstants.S_OK || items == null) { return null; }

        var lines = new List<string>();
        var one = new IVsTaskItem[1];
        while (lines.Count < MaxRows && items.Next(1, one, null) == VSConstants.S_OK && one[0] != null)
        {
            try
            {
                one[0].Document(out var file);
                one[0].Line(out var line);
                one[0].get_Text(out var description);

                var severity = one[0] is IVsErrorItem e && e.GetCategory(out var cat) == VSConstants.S_OK
                    ? SeverityOf(cat)
                    : "Item";
                var where = string.IsNullOrEmpty(file)
                    ? ""
                    // IVsTaskItem lines are 0-based, unlike ErrorItem.Line.
                    : $" in {file}:{line + 1}";
                lines.Add($"{severity}{where}: {System.Net.WebUtility.HtmlDecode(description ?? "")}");
            }
            catch (Exception) { /* row went away mid-read */ }
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string SeverityOf(uint category) => category switch
    {
        (uint)__VSERRORCATEGORY.EC_ERROR => "Error",
        (uint)__VSERRORCATEGORY.EC_WARNING => "Warning",
        _ => "Message",
    };

    private static string Format(ErrorItem err)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var parts = new List<string> { IdeContextService.SeverityToLsp(err.ErrorLevel) };

        // A row can have no file behind it — a project-level error, as GetDiagnosticsAsync found.
        var file = err.FileName;
        if (!string.IsNullOrEmpty(file))
        {
            parts.Add(err.Line > 0 ? $"in {file}:{err.Line}" : $"in {file}");
        }

        if (!string.IsNullOrEmpty(err.Project)) { parts.Add($"({err.Project})"); }

        // HTML-decode for the same reason GetDiagnosticsAsync does it: the Error List sometimes
        // returns XAML-prepared descriptions, and `&quot;` reads as itself to the model.
        var description = System.Net.WebUtility.HtmlDecode(err.Description ?? "");
        return $"{string.Join(" ", parts)}: {description}";
    }
}

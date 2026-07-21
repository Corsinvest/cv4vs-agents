/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// Go-to-definition via the per-language INavigableItemsService.
internal sealed partial class IdeNavigationService
{
    public sealed class NavResult
    {
        /// <summary>False when this VS / language can't be queried this way (no Roslyn
        /// document for the file, or the internal service shape changed). The tool maps
        /// this to a "language not supported" answer so the model falls back to grep.</summary>
        public bool Supported { get; set; }
        public bool Found { get; set; }
        public NavLocation[] Locations { get; set; } = [];
        public string Reason { get; set; }
    }

    private bool _defProbed;
    private bool _defAvailable;
    private Type _navigableItemsServiceType;          // internal INavigableItemsService
    private System.Reflection.MethodInfo _getNavigableItemsAsync;

    private bool EnsureDefProbed()
    {
        if (_defProbed) { return _defAvailable; }
        _defProbed = true;
        if (!EnsureProbed()) { return false; }
        try
        {
            string step = "INavigableItemsService";
            _navigableItemsServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.Navigation.INavigableItemsService");
            if (_navigableItemsServiceType == null) { return ProbeFailed(step); }

            step = "GetNavigableItemsAsync";
            _getNavigableItemsAsync = _navigableItemsServiceType.GetMethods()
                .FirstOrDefault(m => m.Name == "GetNavigableItemsAsync" && m.GetParameters().Length == 3);
            if (_getNavigableItemsAsync == null) { return ProbeFailed(step); }

            _defAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.EnsureDefProbed", ex);
            return false;
        }
    }

    /// <summary>Find where the symbol named <paramref name="symbolName"/> (occurring on
    /// 1-based <paramref name="line"/> of <paramref name="filePath"/>) is defined. The
    /// file must belong to a project in the current solution. Never throws.</summary>
    public async Task<NavResult> GetDefinitionAsync(
        string filePath, int line, string symbolName, CancellationToken ct)
    {
        if (!EnsureDefProbed())
        {
            return new NavResult { Supported = false, Reason = "Navigation services not available in this Visual Studio." };
        }

        try
        {
            var document = ResolveDocument(filePath);
            if (document == null) { return new NavResult { Supported = false, Reason = "No language document for this file (language not supported)." }; }

            var offset = await ResolveOffsetAsync(document, line, symbolName, ct).ConfigureAwait(false);
            if (offset < 0) { return new NavResult { Supported = true, Found = false, Reason = "Symbol not found on that line." }; }

            var service = GetLanguageService(document, _navigableItemsServiceType);
            if (service == null) { return new NavResult { Supported = false, Reason = "This language has no navigation service." }; }

            // GetNavigableItemsAsync(document, position, ct) → Task<ImmutableArray<INavigableItem>>.
            // The MethodInfo is cached (resolved in EnsureDefProbed), so invoke it directly.
            var task = (Task)_getNavigableItemsAsync.Invoke(service, [document, offset, ct]);
            await task.ConfigureAwait(false);
            var items = (IEnumerable)VsReflection.GetProp(task, "Result");

            var locations = ItemsToLocations(items);
            return new NavResult
            {
                Supported = true,
                Found = locations.Length > 0,
                Locations = locations,
                Reason = locations.Length > 0 ? null : "No definition found.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.GetDefinitionAsync", ex);
            return new NavResult { Supported = false, Reason = "Navigation failed (internal API changed?)." };
        }
    }

    /// <summary>Map the navigable items (internal INavigableItem) to public file/line/col.
    /// Reads each target file from disk (a definition may be in a different file).</summary>
    private static NavLocation[] ItemsToLocations(IEnumerable items)
    {
        var result = new System.Collections.Generic.List<NavLocation>();
        foreach (var item in items.Cast<object>())
        {
            var navDoc = VsReflection.GetProp(item, "Document");      // NavigableDocument
            var filePath = (string)VsReflection.GetProp(navDoc, "FilePath");
            if (string.IsNullOrEmpty(filePath)) { continue; } // metadata / generated — skip for now

            var sourceSpan = VsReflection.GetProp(item, "SourceSpan"); // TextSpan
            var spanStart = VsReflection.GetProp<int>(sourceSpan, "Start");

            var (lineNo, colNo, preview) = FileOffsetToLineCol(filePath, spanStart);
            result.Add(new NavLocation { FilePath = filePath, Line = lineNo, Column = colNo, Preview = preview });
        }
        // Stable order (a symbol may have multiple definitions, e.g. partial types).
        return [.. result
            .OrderBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Line).ThenBy(l => l.Column)];
    }
}

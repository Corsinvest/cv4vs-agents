/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// File outline (classes/methods/…) via the per-language INavigationBarItemService — the same
// service behind the editor's navigation dropdown.
internal sealed partial class IdeNavigationService
{
    /// <summary>One node in a file's symbol outline (class → methods, etc.).</summary>
    public sealed class DocSymbol
    {
        public string Name { get; set; }
        public string Kind { get; set; }  // Class/Method/Property/… (from the glyph); null if unknown
        public int Line { get; set; }     // 1-based; 0 when unknown
        public DocSymbol[] Children { get; set; } = [];
    }

    public sealed class SymbolsResult
    {
        public bool Supported { get; set; }
        public DocSymbol[] Symbols { get; set; } = [];
        public string Reason { get; set; }
    }

    private bool _symbolsProbed;
    private bool _symbolsAvailable;
    private Type _navBarServiceType;                 // internal INavigationBarItemService
    private MethodInfo _getNavBarItemsAsync;         // on the service

    private bool EnsureSymbolsProbed()
    {
        if (_symbolsProbed) { return _symbolsAvailable; }
        _symbolsProbed = true;
        if (!EnsureProbed()) { return false; }
        try
        {
            string step = "INavigationBarItemService";
            _navBarServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.NavigationBar.INavigationBarItemService");
            if (_navBarServiceType == null) { return ProbeFailed(step); }

            step = "GetItemsAsync";
            _getNavBarItemsAsync = _navBarServiceType.GetMethods()
                .FirstOrDefault(m => m.Name == "GetItemsAsync" && m.GetParameters().Length == 4);
            if (_getNavBarItemsAsync == null) { return ProbeFailed(step); }

            _symbolsAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.EnsureSymbolsProbed", ex);
            return false;
        }
    }

    /// <summary>List the symbols (classes/methods/…) of a file as a tree. Multi-language;
    /// never throws (degrades to Supported=false). The file must be in the open solution.</summary>
    public async Task<SymbolsResult> GetDocumentSymbolsAsync(string filePath, CancellationToken ct)
    {
        if (!EnsureSymbolsProbed())
        {
            return new SymbolsResult { Supported = false, Reason = "Document symbols not available in this Visual Studio." };
        }

        try
        {
            var document = ResolveDocument(filePath);
            if (document == null) { return new SymbolsResult { Supported = false, Reason = "No language document for this file (language not supported)." }; }

            var service = GetLanguageService(document, _navBarServiceType);
            if (service == null) { return new SymbolsResult { Supported = false, Reason = "This language has no navigation-bar service." }; }

            // GetItemsAsync(document, supportsCodeGeneration:false, frozenPartialSemantics:false, ct)
            var task = (Task)_getNavBarItemsAsync.Invoke(service, [document, false, false, ct]);
            await task.ConfigureAwait(false);
            var items = (IEnumerable)VsReflection.GetProp(task, "Result");

            // Read the document text once to turn span starts into 1-based line numbers.
            var text = await GetTextAsync(document, ct).ConfigureAwait(false);
            // Roslyn returns nav-bar items unordered; sort by position so the outline
            // mirrors the file top-to-bottom (deterministic, easier for the model).
            var symbols = items.Cast<object>().Select(i => MapNavBarItem(i, text)).Where(s => s != null)
                .OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();
            return new SymbolsResult { Supported = true, Symbols = symbols, Reason = symbols.Length > 0 ? null : "No symbols found." };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.GetDocumentSymbolsAsync", ex);
            return new SymbolsResult { Supported = false, Reason = "Document symbols failed (internal API changed?)." };
        }
    }

    /// <summary>Map a RoslynNavigationBarItem (Text + ChildItems; SymbolItem carries a
    /// Location with InDocumentInfo.navigationSpan) to our DocSymbol tree. Sorts children by
    /// line/name at every level (the recursion orders the whole tree).</summary>
    private static DocSymbol MapNavBarItem(object item, object sourceText)
    {
        try
        {
            var name = (string)VsReflection.GetField(item, "Text");
            int line = 0;

            // SymbolItem.Location.InDocumentInfo = (spans, navigationSpan); take navigationSpan.Start.
            var loc = VsReflection.GetField(item, "Location");
            if (loc != null)
            {
                var inDoc = VsReflection.GetField(loc, "InDocumentInfo"); // nullable tuple
                if (inDoc != null)
                {
                    var navSpan = VsReflection.GetField(inDoc, "Item2"); // navigationSpan (TextSpan)
                    if (navSpan != null)
                    {
                        var start = VsReflection.GetProp<int>(navSpan, "Start");
                        line = OffsetToLine(sourceText, start);
                    }
                }
            }

            // Glyph (enum) names the kind, e.g. "ClassPublic", "MethodProtected" — strip the
            // trailing accessibility so we report just "Class"/"Method"/… (any language).
            var glyph = VsReflection.GetField(item, "Glyph");
            var kind = NormalizeGlyph(glyph?.ToString());

            var childItems = VsReflection.GetField(item, "ChildItems") as IEnumerable;
            var children = childItems == null
                ? []
                : childItems.Cast<object>().Select(c => MapNavBarItem(c, sourceText)).Where(s => s != null)
                    .OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray();

            return string.IsNullOrEmpty(name) && children.Length == 0
                ? null
                : new DocSymbol { Name = name ?? "", Kind = kind, Line = line, Children = children };
        }
        catch { return null; }
    }

    private static readonly string[] _accessibilitySuffixes =
        { "Public", "Private", "Protected", "Internal", "ProtectedAndInternal", "ProtectedOrInternal", "Friend" };

    /// <summary>Turn a Glyph name (e.g. "MethodProtected") into a bare kind ("Method").
    /// Null/unknown → null.</summary>
    private static string NormalizeGlyph(string glyph)
    {
        if (string.IsNullOrEmpty(glyph)) { return null; }
        foreach (var suffix in _accessibilitySuffixes)
        {
            if (glyph.EndsWith(suffix, StringComparison.Ordinal) && glyph.Length > suffix.Length)
            {
                return glyph.Substring(0, glyph.Length - suffix.Length);
            }
        }
        return glyph;
    }
}

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

    // Roslyn declares the same interface name twice, in two layers, and a language may register
    // only one of them: C#/VB both, F# and TypeScript only the editor one (measured — F# answers
    // there with items while the Features layer has no service at all). Asking Features alone is
    // what made those two report "this language has no navigation-bar service".
    private Type _editorNavBarServiceType;           // Microsoft.CodeAnalysis.Editor.INavigationBarItemService
    private MethodInfo _getEditorNavBarItemsAsync;   // same call, plus an ITextVersion

    private bool EnsureSymbolsProbed()
    {
        lock (_probeGate)
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

                // The editor-layer twin is a bonus, not a requirement: without it we simply keep
                // answering for the languages the Features layer covers.
                _editorNavBarServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.Editor.INavigationBarItemService");
                _getEditorNavBarItemsAsync = _editorNavBarServiceType?.GetMethods()
                    .FirstOrDefault(m => m.Name == "GetItemsAsync" && m.GetParameters().Length == 5);

                _symbolsAvailable = true;
                return true;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureSymbolsProbed", ex);
                return false;
            }
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

            // A file Roslyn does not have is not necessarily one the IDE cannot outline: a .vcxproj
            // registers no Roslyn project at all, yet DTE's code model answers for it.
            if (document == null)
            {
                var codeModelSymbols = await GetCodeModelSymbolsAsync(filePath, ct).ConfigureAwait(false);
                return codeModelSymbols == null
                    ? new SymbolsResult
                    {
                        Supported = false,
                        Reason = "No language document for this file (language not supported)."
                    }
                    : new SymbolsResult
                    {
                        Supported = true,
                        Symbols = codeModelSymbols,
                        Reason = codeModelSymbols.Length > 0 ? null : "No symbols found.",
                    };
            }

            var items = await GetNavBarItemsAsync(document, ct).ConfigureAwait(false);
            if (items == null) { return new SymbolsResult { Supported = false, Reason = "This language has no navigation-bar service." }; }

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
            OutputWindowLogger.Global.LogException("IdeNavigationService.GetDocumentSymbolsAsync", ex);
            return new SymbolsResult { Supported = false, Reason = "Document symbols failed (internal API changed?)." };
        }
    }

    /// <summary>The file's nav-bar items, from whichever layer this language registers: the
    /// Features one first (C#/VB), then the editor one (F#, TypeScript). Null when neither
    /// answers — which is the only case that is really "not supported".</summary>
    private async Task<IEnumerable> GetNavBarItemsAsync(object document, CancellationToken ct)
    {
        var service = GetLanguageService(document, _navBarServiceType);
        if (service != null)
        {
            // GetItemsAsync(document, supportsCodeGeneration:false, frozenPartialSemantics:false, ct)
            var task = (Task)_getNavBarItemsAsync.Invoke(service, [document, false, false, ct]);
            await task.ConfigureAwait(false);
            return (IEnumerable)VsReflection.GetProp(task, "Result");
        }

        if (_editorNavBarServiceType == null || _getEditorNavBarItemsAsync == null) { return null; }
        var editorService = GetLanguageService(document, _editorNavBarServiceType);
        if (editorService == null) { return null; }

        // The overload takes the ITextVersion its spans are relative to, which only exists for a
        // file open in an editor buffer — and these tools read closed files. It serves callers that
        // re-map spans onto a buffer that has moved on since; a one-shot read never does, and the
        // items come back carrying a version of their own. So pass what we have, null included,
        // rather than open the document to manufacture one.
        var textVersion = await GetTextVersionAsync(document, ct).ConfigureAwait(false);

        try
        {
            var editorTask = (Task)_getEditorNavBarItemsAsync.Invoke(
                editorService, [document, false, false, textVersion, ct]);
            await editorTask.ConfigureAwait(false);
            return (IEnumerable)VsReflection.GetProp(editorTask, "Result");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn(
                $"[nav] editor-layer navigation bar failed: {ex.InnerException?.GetType().Name ?? ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>The ITextVersion behind a Roslyn document, via its text container's buffer.
    /// Null when the document is not backed by an editor buffer.</summary>
    private static async Task<object> GetTextVersionAsync(object document, CancellationToken ct)
    {
        try
        {
            var text = await VsReflection.InvokeAsync(document, "GetTextAsync", ct);   // SourceText
            var container = VsReflection.GetProp(text, "Container");
            // Microsoft.CodeAnalysis.Text.Extensions.TryGetTextBuffer(container) — an extension
            // method on the editor side, so it is reached by name rather than on the instance.
            var extensions = VsReflection.FindType("Microsoft.CodeAnalysis.Text.Extensions");
            var tryGet = extensions?.GetMethod("TryGetTextBuffer");
            var buffer = tryGet?.Invoke(null, [container]);
            var snapshot = buffer == null ? null : VsReflection.GetProp(buffer, "CurrentSnapshot");
            return snapshot == null ? null : VsReflection.GetProp(snapshot, "Version");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.GetTextVersionAsync", ex);
            return null;
        }
    }

    /// <summary>Map a RoslynNavigationBarItem (Text + ChildItems; SymbolItem carries a
    /// Location with InDocumentInfo.navigationSpan) to our DocSymbol tree. Sorts children by
    /// line/name at every level (the recursion orders the whole tree).</summary>
    private static DocSymbol MapNavBarItem(object item, object sourceText)
    {
        try
        {
            var name = (string)Member(item, "Text");
            int line = 0;

            // The two layers carry the position differently, and which one answered depends on the
            // language: RoslynNavigationBarItem (Features — C#/VB) keeps it in
            // Location.InDocumentInfo, SimpleNavigationBarItem (editor — F#, TypeScript) in Spans.
            // Everything else — Text, Glyph, ChildItems — is named the same on both.
            var loc = VsReflection.GetField(item, "Location");
            var inDoc = loc == null ? null : VsReflection.GetField(loc, "InDocumentInfo"); // nullable tuple
            var navSpan = inDoc == null ? null : VsReflection.GetField(inDoc, "Item2");    // navigationSpan
            if (navSpan == null)
            {
                var spans = VsReflection.GetProp(item, "Spans") as IEnumerable;
                navSpan = spans?.Cast<object>().FirstOrDefault();
            }
            if (navSpan != null)
            {
                line = OffsetToLine(sourceText, VsReflection.GetProp<int>(navSpan, "Start"));
            }

            // Glyph (enum) names the kind, e.g. "ClassPublic", "MethodProtected" — strip the
            // trailing accessibility so we report just "Class"/"Method"/… (any language).
            var glyph = Member(item, "Glyph");
            var kind = NormalizeGlyph(glyph?.ToString());

            var childItems = Member(item, "ChildItems") as IEnumerable;
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

    /// <summary>A member by name, field or property: the Features layer declares Text/Glyph/
    /// ChildItems as fields, the editor layer as properties, and the mapper reads both shapes.</summary>
    private static object Member(object obj, string name)
        => VsReflection.GetField(obj, name) ?? VsReflection.GetProp(obj, name);

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

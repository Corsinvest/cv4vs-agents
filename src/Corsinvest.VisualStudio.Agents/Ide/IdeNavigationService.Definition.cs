/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
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
    private MethodInfo _getNavigableItemsAsync;

    private bool _metaProbed;
    private bool _metaAvailable;
    private object _metadataAsSourceService;          // internal IMetadataAsSourceFileService (MEF)
    private MethodInfo _isNavigableMetadataSymbol;
    private MethodInfo _getGeneratedFileAsync;
    private MethodInfo _findSymbolAtPosition;         // SymbolFinder.FindSymbolAtPositionAsync
    private object _metadataAsSourceOptions;          // MetadataAsSourceOptions.Default

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
            OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureDefProbed", ex);
            return false;
        }
    }

    /// <summary>Resolve the metadata-as-source path — the second half of VS's own F12, which turns
    /// a symbol with no source in the solution into a generated file on disk. Everything here is
    /// optional: failing leaves go-to-definition exactly as it was.</summary>
    private bool EnsureMetadataProbed()
    {
        if (_metaProbed) { return _metaAvailable; }
        _metaProbed = true;
        if (!EnsureProbed()) { return false; }
        try
        {
            string step = "IMetadataAsSourceFileService";
            var serviceType = VsReflection.FindType("Microsoft.CodeAnalysis.MetadataAsSource.IMetadataAsSourceFileService");
            if (serviceType == null) { return ProbeFailed(step); }

            // A plain [Export], so the same MEF hop as VisualStudioWorkspace — not a per-document
            // language service like the rest of this file.
            step = "IMetadataAsSourceFileService instance";
            var compModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            _metadataAsSourceService = compModel == null
                ? null
                : typeof(IComponentModel).GetMethod("GetService").MakeGenericMethod(serviceType).Invoke(compModel, null);
            if (_metadataAsSourceService == null) { return ProbeFailed(step); }

            step = "IsNavigableMetadataSymbol / GetGeneratedFileAsync";
            _isNavigableMetadataSymbol = serviceType.GetMethod("IsNavigableMetadataSymbol");
            _getGeneratedFileAsync = serviceType.GetMethod("GetGeneratedFileAsync");
            if (_isNavigableMetadataSymbol == null || _getGeneratedFileAsync == null) { return ProbeFailed(step); }

            // Default already enables decompilation and SourceLink, so no flags to build.
            step = "MetadataAsSourceOptions.Default";
            _metadataAsSourceOptions = VsReflection.FindType("Microsoft.CodeAnalysis.MetadataAsSource.MetadataAsSourceOptions")
                ?.GetField("Default", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (_metadataAsSourceOptions == null) { return ProbeFailed(step); }

            // Public API and in PublicAPI.Shipped.txt, unlike everything else reached here.
            step = "SymbolFinder.FindSymbolAtPositionAsync";
            _findSymbolAtPosition = VsReflection.FindType("Microsoft.CodeAnalysis.FindSymbols.SymbolFinder")
                ?.GetMethods()
                .FirstOrDefault(m => m.Name == "FindSymbolAtPositionAsync" && m.GetParameters().Length == 3);
            if (_findSymbolAtPosition == null) { return ProbeFailed(step); }

            _metaAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureMetadataProbed", ex);
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

            // The navigation service only covers what has source in the solution, so a symbol from
            // a referenced assembly comes back empty. That is where VS's own F12 does not stop.
            if (locations.Length == 0)
            {
                locations = await MetadataDefinitionAsync(document, offset, ct).ConfigureAwait(false);
            }

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
            OutputWindowLogger.Global.LogException("IdeNavigationService.GetDefinitionAsync", ex);
            return new NavResult { Supported = false, Reason = "Navigation failed (internal API changed?)." };
        }
    }

    /// <summary>Map the navigable items (internal INavigableItem) to public file/line/col.
    /// Reads each target file from disk (a definition may be in a different file).</summary>
    private static NavLocation[] ItemsToLocations(IEnumerable items)
    {
        var result = new System.Collections.Generic.List<NavLocation>();
        var seen = 0;
        foreach (var item in items.Cast<object>())
        {
            seen++;
            var navDoc = VsReflection.GetProp(item, "Document");      // NavigableDocument
            var filePath = (string)VsReflection.GetProp(navDoc, "FilePath");
            if (string.IsNullOrEmpty(filePath)) { continue; } // no file to point at

            var sourceSpan = VsReflection.GetProp(item, "SourceSpan"); // TextSpan
            var spanStart = VsReflection.GetProp<int>(sourceSpan, "Start");

            var (lineNo, colNo, preview) = FileOffsetToLineCol(filePath, spanStart);
            result.Add(new NavLocation { FilePath = filePath, Line = lineNo, Column = colNo, Preview = preview });
        }
        // Tells "the service found nothing" apart from "we dropped what it found".
        OutputWindowLogger.Global.Debug(() => $"[nav] definition: {seen} item(s), {result.Count} mapped");

        // Stable order (a symbol may have multiple definitions, e.g. partial types).
        return [.. result
            .OrderBy(l => l.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.Line).ThenBy(l => l.Column)];
    }

    /// <summary>The definition of a symbol that has no source in the solution: Roslyn writes the
    /// decompiled (or SourceLink-fetched) declaration to a file under %TEMP% and hands back its
    /// path. Empty on anything unexpected — the caller then reports "no definition" as before.
    /// <para>
    /// Needs an <c>ISymbol</c>, which only exists where the document has a semantic model, so this
    /// answers for C#/VB and stays out of the way elsewhere. That is not a shortcut past an
    /// agnostic route: no per-document service returns anything at all for metadata symbols, and
    /// "decompile the referenced assembly" has no meaning without .NET metadata to decompile.
    /// </para></summary>
    private async Task<NavLocation[]> MetadataDefinitionAsync(object document, int offset, CancellationToken ct)
    {
        if (!EnsureMetadataProbed()) { return []; }
        try
        {
            // The language gate, and what keeps FindSymbolAtPositionAsync from throwing.
            if (VsReflection.GetPropOrNull(document, "SupportsSemanticModel") is bool supported && !supported)
            {
                return [];
            }

            var symbolTask = (Task)_findSymbolAtPosition.Invoke(null, [document, offset, ct]);
            await symbolTask.ConfigureAwait(false);
            var symbol = VsReflection.GetPropOrNull(symbolTask, "Result");
            if (symbol == null) { return []; }

            if (_isNavigableMetadataSymbol.Invoke(_metadataAsSourceService, [symbol]) is bool navigable && !navigable)
            {
                return [];
            }

            // signaturesOnly:false asks for real bodies; the decompiler ships with VS and the
            // result is cached on disk, so the ~2s is paid once per assembly.
            var project = VsReflection.GetProp(document, "Project");
            var fileTask = (Task)_getGeneratedFileAsync.Invoke(_metadataAsSourceService,
                [_workspace, project, symbol, false, _metadataAsSourceOptions, ct]);
            await fileTask.ConfigureAwait(false);

            var generated = VsReflection.GetPropOrNull(fileTask, "Result");
            var path = (string)VsReflection.GetPropOrNull(generated, "FilePath");
            if (string.IsNullOrEmpty(path)) { return []; }

            // IdentifierLocation is a Roslyn Location, and its line/character are 0-based.
            var lineSpan = VsReflection.Invoke(VsReflection.GetProp(generated, "IdentifierLocation"), "GetLineSpan");
            var start = VsReflection.GetProp(lineSpan, "StartLinePosition");
            var lineNo = VsReflection.GetProp<int>(start, "Line") + 1;
            var colNo = VsReflection.GetProp<int>(start, "Character") + 1;

            OutputWindowLogger.Global.Debug(() => $"[nav] definition from metadata: {path}");

            return
            [
                new NavLocation
                {
                    FilePath = path,
                    Line = lineNo,
                    Column = colNo,
                    Preview = SourceLineAt(path, lineNo),
                    Source = MetadataSourceKind(path),
                }
            ];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Reflection over an internal API: a change of shape must cost the extra answer, not
            // the ordinary one, so the caller still reports what it had.
            OutputWindowLogger.Global.LogException("IdeNavigationService.MetadataDefinitionAsync", ex);
            return [];
        }
    }

    /// <summary>Whether the generated file was decompiled from IL or fetched as real source. The
    /// provider that produced it names the folder it writes into, which is the only reliable
    /// marker: the file itself carries no directive, and DocumentTitle says it but is localized.</summary>
    private static string MetadataSourceKind(string path)
        => path.IndexOf("PdbSourceDocument", StringComparison.OrdinalIgnoreCase) >= 0 ? "source" : "decompiled";

    /// <summary>The text of a 1-based line, trimmed — the preview for a file we have no offset
    /// into (FileOffsetToLineCol works the other way round).</summary>
    private static string SourceLineAt(string filePath, int line)
    {
        try
        {
            var lines = System.IO.File.ReadAllLines(filePath);
            return line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : null;
        }
        catch { return null; }
    }
}

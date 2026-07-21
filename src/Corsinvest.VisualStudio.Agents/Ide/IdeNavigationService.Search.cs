/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// Workspace-wide symbol search via the per-language INavigateToSearchService — same service
// behind VS "Navigate To" (Ctrl+,). Internal Roslyn API → reflection like the other partials.
internal sealed partial class IdeNavigationService
{
    public sealed class SymbolHit
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string File { get; set; }
        public int Line { get; set; }       // 1-based
        public string ContainerName { get; set; }
    }

    public sealed class SearchResult
    {
        public bool Supported { get; set; }
        public string Reason { get; set; }
        public SymbolHit[] Hits { get; set; } = [];
    }

    private bool _searchProbed;
    private bool _searchAvailable;
    private Type _navigateToServiceType;              // internal INavigateToSearchService
    private MethodInfo _searchProjectAsync;           // resolved method on the service
    private bool _searchIsPushBased;                  // true → callback param, false → returns IEnumerable

    private bool EnsureSearchProbed()
    {
        if (_searchProbed) { return _searchAvailable; }
        _searchProbed = true;
        if (!EnsureProbed()) { return false; }
        try
        {
            string step = "INavigateToSearchService";
            _navigateToServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.NavigateTo.INavigateToSearchService");
            if (_navigateToServiceType == null) { return ProbeFailed(step); }

            step = "SearchProjectAsync";
            // Signature varies by Roslyn version — find first method named SearchProjectAsync.
            // The brittle part (param mapping) is isolated in InvokeNavigateToSearchAsync.
            _searchProjectAsync = _navigateToServiceType.GetMethods()
                .FirstOrDefault(m => m.Name == "SearchProjectAsync");
            if (_searchProjectAsync == null) { return ProbeFailed(step); }

            // Detect push-based API: if one of the parameters is a callback/action/func.
            _searchIsPushBased = _searchProjectAsync.GetParameters()
                .Any(p => p.ParameterType.FullName != null &&
                     (p.ParameterType.FullName.Contains("Action") ||
                      p.ParameterType.FullName.Contains("Func")));

            _searchAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.EnsureSearchProbed", ex);
            return false;
        }
    }

    /// <summary>Search for a symbol by name across the whole solution (VS "Navigate To").
    /// Multi-language; never throws (degrades to Supported=false).</summary>
    public async Task<SearchResult> SearchWorkspaceAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResult { Supported = true, Reason = "empty query" };
        }
        if (!EnsureSearchProbed())
        {
            return new SearchResult { Supported = false, Reason = "NavigateTo service unavailable in this Visual Studio." };
        }
        // Push-based API passes results via a callback whose parameter is an internal Roslyn type;
        // we cannot build a valid delegate wrapper, so degrade honestly rather than returning 0 hits.
        if (_searchIsPushBased)
        {
            return new SearchResult { Supported = false, Reason = "NavigateTo push-based search API not supported (callback wrapper unavailable)." };
        }

        try
        {
            var solution = VsReflection.GetProp(_workspace, "CurrentSolution");
            var projects = (IEnumerable)VsReflection.GetProp(solution, "Projects");
            var hits = new List<SymbolHit>();

            foreach (var project in projects.Cast<object>())
            {
                ct.ThrowIfCancellationRequested();

                // Resolve per-project language services.
                var langServices = VsReflection.GetPropOrNull(project, "Services")
                                   ?? VsReflection.GetPropOrNull(project, "LanguageServices");
                if (langServices == null) { continue; }

                var svc = _getServiceGeneric.MakeGenericMethod(_navigateToServiceType).Invoke(langServices, null);
                if (svc == null) { continue; } // language has no NavigateTo provider

                var fromProject = await InvokeNavigateToSearchAsync(svc, project, solution, query, ct)
                    .ConfigureAwait(false);
                hits.AddRange(fromProject);
                if (hits.Count >= 50) { break; }
            }

            var ordered = hits
                .OrderBy(h => h.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Line)
                .Take(50)
                .ToArray();
            return new SearchResult { Supported = true, Hits = ordered };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeNavigationService.SearchWorkspaceAsync", ex);
            return new SearchResult { Supported = false, Reason = "Search failed (internal API changed?)." };
        }
    }

    // Encapsulates the brittle reflection call so a signature mismatch stays isolated.
    // Returns [] if the signature can't be matched — caller stays Supported=true but 0 hits.
    private async Task<List<SymbolHit>> InvokeNavigateToSearchAsync(
        object svc, object project, object solution, string query, CancellationToken ct)
    {
        var results = new List<SymbolHit>();
        try
        {
            var @params = _searchProjectAsync.GetParameters();
            var paramCount = @params.Length;

            // Build argument array by matching parameter types heuristically.
            // Roslyn versions differ on: kinds (IImmutableSet<string> vs ImmutableHashSet<string>),
            // priorityDocuments (IReadOnlyList<Document>), activeDocument (Document), callback.
            var args = new object[paramCount];
            for (int i = 0; i < paramCount; i++)
            {
                var p = @params[i];
                var pType = p.ParameterType;
                var pName = p.Name ?? "";

                if (pType == project.GetType())
                {
                    args[i] = project;
                }
                else if (pType == typeof(string))
                {
                    args[i] = query;
                }
                else if (pType == typeof(CancellationToken))
                {
                    args[i] = ct;
                }
                else if (pType == typeof(bool))
                {
                    // activeDocument-related bool or similar — false is safe default.
                    args[i] = false;
                }
                else if (pName.IndexOf("document", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         pType.IsClass && !pType.IsGenericType)
                {
                    // activeDocument: Document — null means no active document filter.
                    args[i] = null;
                }
                else if (pType.IsGenericType &&
                         (pType.Name.Contains("ImmutableSet") || pType.Name.Contains("ImmutableHashSet") ||
                          pType.Name.Contains("IImmutableSet") || pType.Name.Contains("HashSet")))
                {
                    // kinds: empty set = "all kinds". Build via ImmutableHashSet<string>.Empty or similar.
                    args[i] = BuildEmptyStringSet(pType);
                }
                else if (pType.IsGenericType &&
                         (pType.Name.StartsWith("IReadOnlyList") || pType.Name.StartsWith("IList") ||
                          pType.Name.StartsWith("List") || pType.Name.StartsWith("IEnumerable") ||
                          pType.Name.StartsWith("IReadOnlyCollection")))
                {
                    // priorityDocuments: empty list = no priority.
                    args[i] = BuildEmptyDocumentList(pType);
                }
                else if (pType.FullName != null &&
                         (pType.FullName.Contains("Action") || pType.FullName.Contains("Func")))
                {
                    // Push-based callback — collect hits into results inline.
                    args[i] = BuildResultCallback(pType, results);
                }
                else
                {
                    // Unknown parameter — use default(T): null for ref types, Activator for value types.
                    args[i] = pType.IsValueType ? VsReflection.CreateInstance(pType) : null;
                }
            }

            var returnObj = _searchProjectAsync.Invoke(svc, args);
            if (returnObj is Task t)
            {
                await t.ConfigureAwait(false);
                // Pull-based: method returns Task<IEnumerable<INavigateToSearchResult>> or similar.
                if (VsReflection.GetPropOrNull(t, "Result") is IEnumerable enumerable)
                {
                    foreach (var item in enumerable.Cast<object>())
                    {
                        var hit = MapNavigateToResult(item);
                        if (hit != null) { results.Add(hit); }
                    }
                }
            }
            // Push-based results are already in `results` via the callback delegate.
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Signature mismatch or API change — log once, return whatever we collected so far.
            OutputWindowLogger.LogException("IdeNavigationService.InvokeNavigateToSearchAsync", ex);
        }
        return results;
    }

    // Map one INavigateToSearchResult (internal) to our SymbolHit. Properties vary by Roslyn version.
    private static SymbolHit MapNavigateToResult(object item)
    {
        try
        {
            var name = (string)(VsReflection.GetPropOrNull(item, "Name")
                        ?? VsReflection.GetPropOrNull(item, "MatchedText"));
            if (string.IsNullOrEmpty(name)) { return null; }

            // Kind: string property or NavigateToItemKind enum.
            var kind = VsReflection.GetPropOrNull(item, "Kind")?.ToString();

            var container = (string)(VsReflection.GetPropOrNull(item, "AdditionalInformation")
                            ?? VsReflection.GetPropOrNull(item, "ContainerName"));

            // NavigableItem: DocumentSpan or NavigableItem with SourceSpan/Document.
            var navItem = VsReflection.GetPropOrNull(item, "NavigableItem");
            if (navItem == null) { return null; }

            string filePath = null;
            int line = 0;

            // Try DocumentSpan.Document.FilePath + SourceSpan.Start.
            var docSpan = VsReflection.GetPropOrNull(navItem, "SourceSpan")
                          ?? VsReflection.GetPropOrNull(navItem, "DocumentSpan");
            if (docSpan != null)
            {
                var doc = VsReflection.GetPropOrNull(docSpan, "Document");
                filePath = (string)VsReflection.GetPropOrNull(doc, "FilePath");
                var span = VsReflection.GetPropOrNull(docSpan, "SourceSpan");
                if (span != null && filePath != null)
                {
                    var start = VsReflection.GetProp<int>(span, "Start");
                    (line, _, _) = FileOffsetToLineCol(filePath, start);
                }
            }

            // Fallback: NavigableItem.Document directly.
            if (filePath == null)
            {
                var doc = VsReflection.GetPropOrNull(navItem, "Document");
                filePath = (string)VsReflection.GetPropOrNull(doc, "FilePath");
                var textSpan = VsReflection.GetPropOrNull(navItem, "SourceSpan");
                if (textSpan != null && filePath != null)
                {
                    var start = VsReflection.GetProp<int>(textSpan, "Start");
                    (line, _, _) = FileOffsetToLineCol(filePath, start);
                }
            }

            return string.IsNullOrEmpty(filePath)
                ? null
                : new SymbolHit { Name = name, Kind = kind, File = filePath, Line = line, ContainerName = container };
        }
        catch { return null; }
    }

    // Build an empty immutable string set compatible with the given type (kinds parameter).
    private static object BuildEmptyStringSet(Type setType)
    {
        try
        {
            // Try ImmutableHashSet<string>.Empty or ImmutableSortedSet<string>.Empty.
            var baseName = setType.IsInterface
                ? setType.GetGenericArguments().Length > 0 ? "System.Collections.Immutable.ImmutableHashSet" : null
                : setType.FullName?.Split('`')[0];
            if (baseName == null) { return null; }
            var concreteType = Type.GetType($"{baseName}`1[[System.String]], System.Collections.Immutable");
            if (concreteType == null)
            {
                // Scan loaded assemblies for ImmutableHashSet<string>.
                concreteType = VsReflection.FindType("System.Collections.Immutable.ImmutableHashSet`1")
                    ?.MakeGenericType(typeof(string));
            }
            return concreteType?.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)
                               ?.GetValue(null);
        }
        catch { return null; }
    }

    // Build an empty list of Documents compatible with the given collection type.
    private static object BuildEmptyDocumentList(Type listType)
    {
        try
        {
            // IReadOnlyList<Document> etc. — Array.Empty<object>() satisfies most covariant interfaces.
            var elemType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
            return Array.CreateInstance(elemType, 0);
        }
        catch { return null; }
    }

    // Build a delegate that feeds INavigateToSearchResult items into results as they arrive.
    private static Delegate BuildResultCallback(Type delegateType, List<SymbolHit> results)
    {
        try
        {
            // The callback is Action<INavigateToSearchResult> (or similar with 1 Roslyn-internal param).
            return Delegate.CreateDelegate(
                delegateType,
                results,
                typeof(List<SymbolHit>).GetMethod(nameof(List<SymbolHit>.Add)));
        }
        catch
        {
            // If the element type doesn't match (e.g. callback takes the internal result type), wrap it.
            return null;
        }
    }
}

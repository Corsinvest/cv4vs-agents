/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// Workspace-wide symbol search via the per-language INavigateToSearchService — same service
// behind VS "Navigate To" (Ctrl+,). Internal Roslyn API → reflection like the other partials.
internal sealed partial class IdeNavigationService
{
    /// <summary>Cap on hits returned to the caller. The search is stopped as soon as it is
    /// reached: NavigateTo streams results and a solution-wide query can produce thousands.</summary>
    private const int MaxHits = 50;

    public sealed class SymbolHit
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string File { get; set; }
        public int Line { get; set; }       // 1-based
        public string ContainerName { get; set; }
        public string Preview { get; set; } // the declaration line, trimmed
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
    private Type _searchResultType;                   // internal INavigateToSearchResult
    private MethodInfo _searchProjectsAsync;          // resolved method on the service
    private Type _projectType;                        // Microsoft.CodeAnalysis.Project
    private Type _documentType;                       // Microsoft.CodeAnalysis.Document

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

            step = "INavigateToSearchResult";
            _searchResultType = VsReflection.FindType("Microsoft.CodeAnalysis.NavigateTo.INavigateToSearchResult");
            if (_searchResultType == null) { return ProbeFailed(step); }

            step = "Project/Document types";
            _projectType = VsReflection.FindType("Microsoft.CodeAnalysis.Project");
            _documentType = VsReflection.FindType("Microsoft.CodeAnalysis.Document");
            if (_projectType == null || _documentType == null) { return ProbeFailed(step); }

            // One call covers the whole solution. Roslyn 5.x: SearchProjectsAsync(solution,
            // projects, priorityDocuments, searchPattern, kinds, activeDocument, onResultsFound,
            // onProjectCompleted, ct). Matched by name only — the arguments are mapped by
            // parameter type in BuildSearchArguments, so a reordering doesn't silently misfire.
            step = "SearchProjectsAsync";
            _searchProjectsAsync = _navigateToServiceType.GetMethods()
                .FirstOrDefault(m => m.Name == "SearchProjectsAsync");
            if (_searchProjectsAsync == null) { return ProbeFailed(step); }

            _searchAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureSearchProbed", ex);
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

        try
        {
            var solution = VsReflection.GetProp(_workspace, "CurrentSolution");
            var projects = ((IEnumerable)VsReflection.GetProp(solution, "Projects")).Cast<object>().ToList();

            // The service is registered per language, so group the projects by the instance that
            // serves them: one call per service covers every project of that language at once.
            var byService = new Dictionary<object, List<object>>();
            foreach (var project in projects)
            {
                var langServices = VsReflection.GetPropOrNull(project, "Services")
                                   ?? VsReflection.GetPropOrNull(project, "LanguageServices");
                if (langServices == null) { continue; }

                var svc = _getServiceGeneric.MakeGenericMethod(_navigateToServiceType).Invoke(langServices, null);
                if (svc == null) { continue; } // language has no NavigateTo provider

                if (!byService.TryGetValue(svc, out var list))
                {
                    list = [];
                    byService[svc] = list;
                }
                list.Add(project);
            }

            if (byService.Count == 0)
            {
                return new SearchResult { Supported = false, Reason = "No project in this solution provides NavigateTo search." };
            }

            var collector = new HitCollector(MaxHits);
            foreach (var pair in byService)
            {
                ct.ThrowIfCancellationRequested();
                if (collector.IsFull) { break; }
                await InvokeNavigateToSearchAsync(pair.Key, solution, pair.Value, query, collector, ct)
                    .ConfigureAwait(false);
            }

            var ordered = collector.Hits
                .OrderBy(h => h.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(h => h.Line)
                .Take(MaxHits)
                .ToArray();
            return new SearchResult { Supported = true, Hits = ordered };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.SearchWorkspaceAsync", ex);
            return new SearchResult { Supported = false, Reason = "Search failed (internal API changed?)." };
        }
    }

    // Encapsulates the brittle reflection call so a signature mismatch stays isolated: on failure
    // the caller keeps whatever the callback already collected.
    private async Task InvokeNavigateToSearchAsync(
        object svc, object solution, List<object> projects, string query, HitCollector collector, CancellationToken ct)
    {
        try
        {
            // The search streams results and only stops when the whole solution has been walked,
            // so cancel it ourselves once MaxHits is reached instead of letting it run to the end.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            collector.StopWhenFull(linked);

            var args = BuildSearchArguments(svc, solution, projects, query, collector, linked.Token);
            if (args == null) { return; }

            await ((Task)_searchProjectsAsync.Invoke(svc, args)).ConfigureAwait(false);

            // Raw vs mapped tells a genuinely empty search apart from results we failed to read.
            OutputWindowLogger.Global.Debug(() =>
                $"[navto] {svc.GetType().Name} over {projects.Count} project(s): " +
                $"{collector.RawCount} raw result(s), {collector.Hits.Count} mapped");
        }
        catch (OperationCanceledException) when (collector.IsFull) { /* our own stop */ }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.InvokeNavigateToSearchAsync", ex);
        }
    }

    /// <summary>Map the arguments onto whatever parameters this Roslyn declares, by type rather
    /// than by position. Returns null if a parameter can't be satisfied — better no results than
    /// a call built on a wrong guess.</summary>
    private object[] BuildSearchArguments(
        object svc, object solution, List<object> projects, string query, HitCollector collector, CancellationToken ct)
    {
        var parameters = _searchProjectsAsync.GetParameters();
        var args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;

            if (type == typeof(string)) { args[i] = query; }
            else if (type == typeof(CancellationToken)) { args[i] = ct; }
            else if (type == solution.GetType()) { args[i] = solution; }
            else if (type == _documentType) { args[i] = null; }               // activeDocument: none
            else if (IsImmutableArrayOf(type, _projectType)) { args[i] = ToImmutableArray(_projectType, projects); }
            else if (IsImmutableArrayOf(type, _documentType)) { args[i] = ToImmutableArray(_documentType, []); }
            // kinds: the service's own KindsProvided means "everything you can find". An empty set
            // is what VS passes for an unfiltered search, but it reads equally as "no kind at all".
            else if (IsStringSet(type)) { args[i] = VsReflection.GetPropOrNull(svc, "KindsProvided"); }
            else if (type == typeof(Func<Task>)) { args[i] = OnProjectCompleted; }
            else if (IsResultsCallback(type)) { args[i] = collector.CreateCallback(type); }
            else
            {
                OutputWindowLogger.Global.Debug(() =>
                    $"[navto] unmapped parameter {parameters[i].Name} of type {type.FullName} — search skipped");
                return null;
            }

            if (args[i] == null && type.IsValueType) { return null; }
        }
        return args;
    }

    private static Task OnProjectCompleted() => Task.CompletedTask;

    private static bool IsImmutableArrayOf(Type type, Type element)
        => type.IsGenericType
           && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)
           && type.GetGenericArguments()[0] == element;

    // kinds: declared IImmutableSet<string> today, but any string set will do.
    private static bool IsStringSet(Type type)
        => type.IsGenericType
           && type.GetGenericArguments().Length == 1
           && type.GetGenericArguments()[0] == typeof(string)
           && type.Name.Contains("Set");

    // Func<ImmutableArray<INavigateToSearchResult>, Task> — the results are pushed in batches.
    private bool IsResultsCallback(Type type)
        => type.IsGenericType
           && type.GetGenericTypeDefinition() == typeof(Func<,>)
           && IsImmutableArrayOf(type.GetGenericArguments()[0], _searchResultType);

    /// <summary>The element type is only known at run time, so the array is built by reflection
    /// and handed to <c>ImmutableArray.Create&lt;T&gt;(T[])</c> closed over it.</summary>
    private static object ToImmutableArray(Type element, IReadOnlyList<object> items)
    {
        var array = Array.CreateInstance(element, items.Count);
        for (int i = 0; i < items.Count; i++) { array.SetValue(items[i], i); }

        var create = typeof(ImmutableArray).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(ImmutableArray.Create) && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType.IsArray);
        return create?.MakeGenericMethod(element).Invoke(null, [array]);
    }

    /// <summary>Receives the batches NavigateTo pushes and maps them to <see cref="SymbolHit"/>.
    /// The callback is <c>Func&lt;ImmutableArray&lt;INavigateToSearchResult&gt;, Task&gt;</c>, and
    /// that element type is internal to Roslyn — so <see cref="OnResultsFound"/> is generic and
    /// gets closed over it by reflection to build a delegate of the exact expected shape.</summary>
    private sealed class HitCollector(int maxHits)
    {
        private readonly List<SymbolHit> _hits = [];
        private CancellationTokenSource _stopWhenFull;

        public IReadOnlyList<SymbolHit> Hits => _hits;
        public bool IsFull => _hits.Count >= maxHits;

        // Counted so a zero-hit search can be told apart from results we failed to read.
        public int RawCount { get; private set; }

        public void StopWhenFull(CancellationTokenSource cts) => _stopWhenFull = cts;

        public Delegate CreateCallback(Type delegateType)
            => Delegate.CreateDelegate(
                delegateType,
                this,
                typeof(HitCollector)
                    .GetMethod(nameof(OnResultsFound), BindingFlags.NonPublic | BindingFlags.Instance)
                    .MakeGenericMethod(delegateType.GetGenericArguments()[0].GetGenericArguments()[0]));

        private Task OnResultsFound<T>(ImmutableArray<T> results)
        {
            RawCount += results.Length;
            foreach (var item in results)
            {
                if (IsFull) { break; }
                var hit = MapNavigateToResult(item);
                if (hit != null) { _hits.Add(hit); }
            }
            if (IsFull) { _stopWhenFull?.Cancel(); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Read a property that the concrete type may implement explicitly. Roslyn returns
    /// results as a private nested type whose members exist only on the interface, so a lookup on
    /// <c>GetType()</c> finds nothing and silently yields null.</summary>
    private static object ReadThroughInterfaces(object obj, string name)
    {
        if (obj == null) { return null; }

        var direct = obj.GetType().GetProperty(name);
        if (direct != null) { return direct.GetValue(obj); }

        foreach (var iface in obj.GetType().GetInterfaces())
        {
            var prop = iface.GetProperty(name);
            if (prop != null) { return prop.GetValue(obj); }
        }
        return null;
    }

    // Map one INavigateToSearchResult (internal) to our SymbolHit.
    private static SymbolHit MapNavigateToResult(object item)
    {
        try
        {
            var name = (string)ReadThroughInterfaces(item, "Name");
            if (string.IsNullOrEmpty(name)) { return null; }

            var kind = (string)ReadThroughInterfaces(item, "Kind");

            // AdditionalInformation is the container as VS shows it ("in ClienteRepository
            // (project X)") — already localized by the language service, so it is taken as is.
            var container = (string)ReadThroughInterfaces(item, "AdditionalInformation");

            // NavigableItem carries the position: Document.FilePath + SourceSpan.Start.
            var navItem = ReadThroughInterfaces(item, "NavigableItem");
            if (navItem == null) { return null; }

            var doc = ReadThroughInterfaces(navItem, "Document");
            var filePath = (string)ReadThroughInterfaces(doc, "FilePath");
            if (string.IsNullOrEmpty(filePath)) { return null; }

            var span = ReadThroughInterfaces(navItem, "SourceSpan");
            var line = 0;
            string preview = null;
            if (span != null)
            {
                var start = VsReflection.GetProp<int>(span, "Start");
                (line, _, preview) = FileOffsetToLineCol(filePath, start);
            }

            return new SymbolHit
            {
                Name = name,
                Kind = kind,
                File = filePath,
                Line = line,
                ContainerName = container,
                Preview = preview,
            };
        }
        catch { return null; }
    }
}

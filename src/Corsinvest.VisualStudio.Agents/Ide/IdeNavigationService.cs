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

/// <summary>
/// <para>
/// Multi-language IDE navigation (go-to-definition, find-references, file symbols, rename)
/// without opening the editor. Works for ANY language whose VS language service registers the
/// relevant per-document service (C#, VB, F#, C++, TS, …) — VS exposes these through Roslyn's
/// <c>Project.Services.GetService&lt;TLanguageService&gt;()</c>, but the service interfaces
/// (e.g. INavigableItemsService) are <c>internal</c> to Microsoft.CodeAnalysis.
/// </para>
/// <para>
/// So the public path (workspace → solution → document → text) is used directly, and ONLY the
/// internal service hop is done via reflection against the Roslyn assemblies VS already has
/// loaded. No NuGet package is referenced (which would risk version conflicts with the host VS
/// edition); we bind to whatever Roslyn the running VS provides. If the expected types/members
/// aren't found (a future VS reshaped them), every call degrades to "not supported" instead of
/// throwing — the caller (MCP tool) then reports that to the model.
/// </para>
/// <para>
/// This file holds the shared core (workspace + GetService&lt;T&gt; probing, type/offset
/// helpers, common result types). Each feature lives in its own partial:
/// <c>IdeNavigationService.Definition/References/Symbols/Rename.cs</c>.
/// </para>
/// </summary>
internal sealed partial class IdeNavigationService
{
    public static IdeNavigationService Instance { get; } = new();

    /// <summary>A resolved location: a file/line/column (and optionally the source line text).
    /// Shared by definition, references and rename-conflict results.</summary>
    public sealed class NavLocation
    {
        public string FilePath { get; set; }
        public int Line { get; set; }       // 1-based
        public int Column { get; set; }     // 1-based
        public string Preview { get; set; } // the source line's text, trimmed

        /// <summary>Null for a file of the solution. Set when the definition lives in a referenced
        /// assembly and had to be generated: "decompiled" (rebuilt from IL — locals renamed,
        /// syntax re-emitted) or "source" (the real thing, via SourceLink).</summary>
        public string Source { get; set; }
    }

    // Shared reflection handles, resolved once (feature-detection). Null ⇒ unsupported.

    /// <summary><para>Guards every EnsureXxxProbed in the partials. They all follow the same
    /// shape — set the "probed" flag, then do the work, then set "available" — which reads as
    /// run-once but is not safe against a second caller: two nav tools invoked in parallel had the
    /// second see probed=true while the first was still resolving, and answer "not available in
    /// this Visual Studio" for a service that works. Two calls to the same tool, seconds apart,
    /// disagreeing.</para>
    /// <para>One lock for all of them: probing is a handful of reflection lookups that happen once
    /// per session, so there is nothing to gain from finer grain, and the base probe is shared by
    /// every feature anyway.</para></summary>
    private readonly object _probeGate = new();

    private bool _probed;
    private bool _available;
    private object _workspace;                       // VisualStudioWorkspace (as object)
    private MethodInfo _getServiceGeneric;           // LanguageServices.GetService<T>() bound per call

    /// <summary>Resolve the base Roslyn handles (workspace + GetService&lt;T&gt;) from the
    /// already-loaded VS assemblies. Each feature's own EnsureXxxProbed calls this first and
    /// then resolves its service. Returns false (and we stay "unsupported") if anything is
    /// missing, so a reshaped future Roslyn can't crash us. Runs once.</summary>
    private bool EnsureProbed()
    {
        lock (_probeGate)
        {
            if (_probed) { return _available; }
            _probed = true;
            try
            {
                // Resolve types by scanning loaded assemblies — Type.GetType("…, AsmName")
                // doesn't bind these VS/Roslyn assemblies (partial assembly name).
                // `step` names the last thing probed, logged once if a future VS reshapes things.
                string step = "workspaceType";
                var compModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                var workspaceType = VsReflection.FindType("Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace");
                if (compModel == null || workspaceType == null) { return ProbeFailed(step); }

                step = "VisualStudioWorkspace instance";
                var getCompService = typeof(IComponentModel).GetMethod("GetService").MakeGenericMethod(workspaceType);
                _workspace = getCompService.Invoke(compModel, null);
                if (_workspace == null) { return ProbeFailed(step); }

                step = "LanguageServices.GetService<T>";
                var languageServicesType = VsReflection.FindType("Microsoft.CodeAnalysis.Host.LanguageServices");
                _getServiceGeneric = languageServicesType?.GetMethods()
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (_getServiceGeneric == null) { return ProbeFailed(step); }

                _available = true;
                return true;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureProbed", ex);
                return false;
            }
        }
    }

    private static bool ProbeFailed(string step)
    {
        OutputWindowLogger.Global.Debug(() => $"[IdeNavigationService] navigation unavailable — probe failed at: {step}");
        return false;
    }

    /// <summary>workspace.CurrentSolution → the Document object for <paramref name="filePath"/>,
    /// or null if the file isn't a Roslyn document in the open solution. Shared resolution used
    /// by every feature.</summary>
    private object ResolveDocument(string filePath)
    {
        var solution = VsReflection.GetProp(_workspace, "CurrentSolution");
        var docIds = (IEnumerable)VsReflection.Invoke(solution, "GetDocumentIdsWithFilePath",
            [typeof(string)], [filePath]);
        var docId = docIds?.Cast<object>().FirstOrDefault();
        return docId == null ? null : VsReflection.Invoke(solution, "GetDocument", [docId.GetType()], [docId]);
    }

    /// <summary>document.Project.Services.GetService&lt;serviceType&gt;() — the per-language
    /// service hop. Null if this language doesn't register the service.</summary>
    private object GetLanguageService(object document, Type serviceType)
    {
        var project = VsReflection.GetProp(document, "Project");
        var services = VsReflection.GetProp(project, "Services"); // LanguageServices
        return _getServiceGeneric.MakeGenericMethod(serviceType).Invoke(services, null);
    }

    /// <summary>Byte offset of <paramref name="symbolName"/> on the given 1-based line, or
    /// -1 if not present. Uses the public SourceText API.</summary>
    private static async Task<int> ResolveOffsetAsync(object document, int line, string symbolName, CancellationToken ct)
    {
        var text = await VsReflection.InvokeAsync(document, "GetTextAsync", ct); // SourceText

        var lines = VsReflection.GetProp(text, "Lines"); // TextLineCollection
        var lineCount = VsReflection.GetProp<int>(lines, "Count");
        var idx = line - 1;
        if (idx < 0 || idx >= lineCount) { return -1; }

        var textLine = VsReflection.GetIndexer(lines, idx); // TextLine
        var start = VsReflection.GetProp<int>(textLine, "Start");

        // Read the line's string and find the symbol within it.
        var spanProp = VsReflection.GetProp(textLine, "Span"); // TextSpan
        var lineText = (string)VsReflection.Invoke(text, "ToString",
            [spanProp.GetType()], [spanProp]);
        if (string.IsNullOrEmpty(symbolName)) { return start; }
        var col = lineText.IndexOf(symbolName, StringComparison.Ordinal);
        return col < 0 ? -1 : start + col;
    }

    /// <summary>1-based line of an offset using the public SourceText.Lines API (object).</summary>
    private static int OffsetToLine(object sourceText, int offset)
    {
        try
        {
            var lines = VsReflection.GetProp(sourceText, "Lines");
            var textLine = VsReflection.Invoke(lines, "GetLineFromPosition",
                [typeof(int)], [offset]);
            return VsReflection.GetProp<int>(textLine, "LineNumber") + 1;
        }
        catch { return 0; }
    }

    /// <summary>Await Document.GetTextAsync and return the SourceText (as object).</summary>
    private static Task<object> GetTextAsync(object document, CancellationToken ct)
        => VsReflection.InvokeAsync(document, "GetTextAsync", ct);

    /// <summary>What one language in the open solution can answer.</summary>
    public sealed class LanguageCoverage
    {
        /// <summary>Roslyn's own name for it: "C#", "Visual Basic", "TypeScript", …</summary>
        public string Language { get; set; }
        public int ProjectCount { get; set; }
        /// <summary>Service name → whether that language registers it. A false here is a gap in
        /// one feature; the language itself is covered.</summary>
        public System.Collections.Generic.Dictionary<string, bool> Services { get; set; } = [];
    }

    /// <summary>Source files a project holds that its own language service does not answer for —
    /// extension → how many. A .csproj carrying .ts and .sql files is a C# project as far as the
    /// workspace is concerned, and the services all report true, but none of them will say a word
    /// about those files. Without this the coverage report is true and still misleading.</summary>
    public sealed class ForeignFiles
    {
        public string Project { get; set; }
        /// <summary>Null for a project outside the workspace: there is no Roslyn language to name,
        /// which is the whole reason it is out of reach.</summary>
        public string ProjectLanguage { get; set; }
        public System.Collections.Generic.Dictionary<string, int> ByExtension { get; set; } = [];
    }

    public sealed class CoverageResult
    {
        public bool Supported { get; set; }
        public string Reason { get; set; }
        public LanguageCoverage[] Languages { get; set; } = [];
        /// <summary>Projects of the solution that are in no Roslyn workspace at all — C++ and the
        /// like. Their languages cannot be reached through any of the services below, whatever
        /// those answer. Carries the extensions too: the project name alone says a project is out
        /// of reach without saying what is in it.</summary>
        public ForeignFiles[] ProjectsOutsideWorkspace { get; set; } = [];

        /// <summary>Source files inside covered projects that their language does not answer for.
        /// Empty when every project holds only its own language.</summary>
        public ForeignFiles[] UncoveredFiles { get; set; } = [];
    }

    /// <summary><para>Which languages this solution holds, and which navigation services each of
    /// them registers — measured, not assumed.</para>
    /// <para>The two questions have different answers and different consequences. A language absent
    /// from <c>CurrentSolution.Projects</c> is outside the Roslyn workspace entirely (C++), and no
    /// amount of asking will help: reaching it needs a separate backend. A language that is present
    /// but whose <c>GetService&lt;T&gt;()</c> returns null for one interface simply lacks that one
    /// feature. Both surface as <c>supported=false</c> from a nav tool, which is why they were
    /// indistinguishable until this was written — the answer took four throwaway projects and a
    /// round of manual calls to work out the first time.</para></summary>
    public CoverageResult GetCoverage()
    {
        if (!EnsureProbed())
        {
            return new CoverageResult { Supported = false, Reason = "Navigation services not available in this Visual Studio." };
        }

        // Probe each feature first: the service types are resolved lazily by the partials, and a
        // null type here would otherwise read as "the language does not have it".
        EnsureDefProbed();
        EnsureRefsProbed();
        EnsureSymbolsProbed();
        EnsureRenameProbed();
        EnsureSearchProbed();

        var wanted = new (string Name, Type Type)[]
        {
            ("go_to_definition", _navigableItemsServiceType),
            ("find_references / go_to_implementation", _findUsagesServiceType),
            ("get_document_symbols", _navBarServiceType),
            ("rename_symbol", _inlineRenameServiceType),
            ("search_workspace_symbols", _navigateToServiceType),
        };

        try
        {
            var byLanguage = new System.Collections.Generic.Dictionary<string, LanguageCoverage>(StringComparer.Ordinal);
            var inWorkspace = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var solution = VsReflection.GetProp(_workspace, "CurrentSolution");
            foreach (var project in ((IEnumerable)VsReflection.GetProp(solution, "Projects")).Cast<object>())
            {
                var language = VsReflection.GetPropOrNull(project, "Language") as string ?? "?";
                if (VsReflection.GetPropOrNull(project, "Name") is string name) { inWorkspace[name] = language; }
                if (!byLanguage.TryGetValue(language, out var coverage))
                {
                    coverage = new LanguageCoverage { Language = language };
                    byLanguage[language] = coverage;

                    // Ask once per language, not once per project: the services are registered
                    // per language, so every project of one answers identically.
                    var services = VsReflection.GetPropOrNull(project, "Services")
                                   ?? VsReflection.GetPropOrNull(project, "LanguageServices");
                    foreach (var (serviceName, serviceType) in wanted)
                    {
                        coverage.Services[serviceName] = serviceType != null
                            && services != null
                            && _getServiceGeneric.MakeGenericMethod(serviceType).Invoke(services, null) != null;
                    }
                }
                coverage.ProjectCount++;
            }

            var (outside, uncovered) = WalkSolution(inWorkspace);
            return new CoverageResult
            {
                Supported = true,
                Languages = [.. byLanguage.Values.OrderBy(l => l.Language, StringComparer.OrdinalIgnoreCase)],
                ProjectsOutsideWorkspace = outside,
                UncoveredFiles = uncovered,
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.GetCoverage", ex);
            return new CoverageResult { Supported = false, Reason = "Could not read the solution's languages." };
        }
    }

    /// <summary><para>One pass over the solution as the IDE sees it, answering both halves of
    /// "what is not covered": the projects the Roslyn workspace never saw, and the source files
    /// sitting inside covered projects that their project's language does not answer for.</para>
    /// <para>Read through the IDE-context walk, which recurses solution folders and drops the
    /// Miscellaneous Files node — a flat pass over DTE's Solution.Projects gets both of those
    /// wrong, as an earlier attempt at this in Search.cs found out by reporting "File esterni".
    /// </para></summary>
    private static (ForeignFiles[] Outside, ForeignFiles[] Uncovered) WalkSolution(
        System.Collections.Generic.Dictionary<string, string> inWorkspace)
    {
        try
        {
            var structure = ThreadHelper.JoinableTaskFactory.Run(
                () => IdeContextService.Instance.GetProjectStructureAsync());

            var outside = new System.Collections.Generic.List<ForeignFiles>();
            var uncovered = new System.Collections.Generic.List<ForeignFiles>();

            foreach (var project in structure.Projects)
            {
                if (string.IsNullOrEmpty(project.Name)) { continue; }
                if (!inWorkspace.TryGetValue(project.Name, out var language))
                {
                    // Nothing in there is reachable, so every source file counts, not just the
                    // ones foreign to a language — there is no language to be foreign to.
                    outside.Add(new ForeignFiles
                    {
                        Project = project.Name,
                        ByExtension = CountExtensions(project.Files, language: null),
                    });
                    continue;
                }

                var foreign = CountExtensions(project.Files, language);
                if (foreign.Count > 0)
                {
                    uncovered.Add(new ForeignFiles
                    {
                        Project = project.Name,
                        ProjectLanguage = language,
                        ByExtension = foreign,
                    });
                }
            }

            return (
                [.. outside.OrderBy(o => o.Project, StringComparer.OrdinalIgnoreCase)],
                [.. uncovered.OrderBy(u => u.Project, StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[nav] could not list the solution's projects: {ex.Message}");
            return ([], []);
        }
    }

    /// <summary>Source files by extension. With a <paramref name="language"/> only the ones that
    /// language does not answer for are counted; with null every source file is, which is the case
    /// for a project no language service reaches at all.</summary>
    private static System.Collections.Generic.Dictionary<string, int> CountExtensions(
        System.Collections.Generic.List<string> files, string language)
    {
        var counts = new System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files ?? [])
        {
            var extension = System.IO.Path.GetExtension(file);
            if (string.IsNullOrEmpty(extension)) { continue; }
            if (language != null && SpeaksFor(language, extension)) { continue; }
            // Only source-looking files: a project's .png, .json and .config are not something
            // anyone expects a nav tool to answer for.
            if (!IsSourceExtension(extension)) { continue; }
            counts.TryGetValue(extension, out var count);
            counts[extension] = count + 1;
        }
        return counts;
    }

    /// <summary>Whether a project of <paramref name="language"/> is the one that answers for files
    /// with this extension. Deliberately a short list rather than IFileExtensionRegistryService:
    /// the question is not "which language is this file" but "does THIS project's service cover
    /// it", and everything outside the list is the answer we are looking for anyway.</summary>
    private static bool SpeaksFor(string language, string extension) => language switch
    {
        "C#" => extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csx", StringComparison.OrdinalIgnoreCase),
        "Visual Basic" => extension.Equals(".vb", StringComparison.OrdinalIgnoreCase),
        "F#" => extension.Equals(".fs", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsx", StringComparison.OrdinalIgnoreCase),
        "TypeScript" => extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase),
        // An unknown language: say yes, so a language we have not met is not reported as a gap.
        _ => true,
    };

    /// <summary>Extensions worth reporting as uncovered — code, not assets.</summary>
    private static bool IsSourceExtension(string extension)
    {
        string[] source =
        [
            ".cs", ".vb", ".fs", ".fsi", ".fsx", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
            ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hxx", ".ipp",
            ".py", ".sql", ".xaml", ".razor", ".cshtml", ".vbhtml", ".css", ".scss", ".less",
        ];
        return source.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>1-based line/column for a byte offset into a file on disk, plus the trimmed source
    /// line. Used when the hit is in a file we don't hold a SourceText for.</summary>
    private static (int line, int col, string preview) FileOffsetToLineCol(string filePath, int offset)
    {
        try
        {
            var content = System.IO.File.ReadAllText(filePath);
            int line = 1, col = 1;
            for (int i = 0; i < offset && i < content.Length; i++)
            {
                if (content[i] == '\n') { line++; col = 1; } else { col++; }
            }
            var lineStart = offset - (col - 1);
            var lineEnd = content.IndexOf('\n', Math.Min(lineStart, content.Length));
            if (lineEnd < 0) { lineEnd = content.Length; }
            var preview = content.Substring(lineStart, Math.Max(0, lineEnd - lineStart)).Trim();
            return (line, col, preview);
        }
        catch { return (0, 0, null); }
    }
}

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
    }

    // ---- Shared reflection handles, resolved once (feature-detection). Null ⇒ unsupported. ----

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
            OutputWindowLogger.LogException("IdeNavigationService.EnsureProbed", ex);
            return false;
        }
    }

    private static bool ProbeFailed(string step)
    {
        OutputWindowLogger.Debug(() => $"[IdeNavigationService] navigation unavailable — probe failed at: {step}");
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

    /// <summary>Read a file from disk and turn a 0-based char offset into 1-based line/column +
    /// the trimmed source line. Used when the hit is in a file we don't hold a SourceText for.</summary>
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

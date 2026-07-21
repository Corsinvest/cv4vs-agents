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
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeContextService, IDE query/action side: the operations the MCP tools call
/// (identity, build, project structure, workspace folders, open editors,
/// diagnostics, open file / diff, format / organize, interactive exec). The
/// live context tracking (active view + selection + emit) lives in
/// IdeContextService.cs.
/// </summary>
internal sealed partial class IdeContextService
{
    //  IDE identity (lock file + UI hints)

    /// <summary>DTE version's major → the VS marketing year (null when unknown).
    /// Single source for both the lock-file ideName and the MCP version/edition tools.</summary>
    private static string MarketingYear(string version)
        => (version?.Split('.').FirstOrDefault()) switch
        {
            "17" => "2022",
            "18" => "2026",
            _ => null,
        };

    /// <summary>Friendly name of the running IDE for the lock file's
    /// <c>ideName</c> field. Maps DTE's numeric version to the marketing
    /// name when known — users see "Visual Studio 2026" instead of
    /// "Visual Studio 18.0".</summary>
    public async Task<string> GetIdeNameAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var version = (Package.GetGlobalService(typeof(DTE)) as DTE)?.Version;
        if (string.IsNullOrEmpty(version)) { return "Visual Studio"; }
        return $"Visual Studio {MarketingYear(version) ?? version}";
    }

    /// <summary>Visual Studio version details for the MCP version/edition tools:
    /// marketing name ("Visual Studio 2026"), marketing year ("2026"), raw DTE
    /// version ("18.0"), and edition ("Enterprise"/"Professional"/"Community").</summary>
    public async Task<(string Name, string Year, string Version, string Edition)> GetIdeInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var version = dte?.Version ?? "";
        var edition = dte?.Edition ?? "";
        var year = MarketingYear(version);
        var name = year == null
            ? (string.IsNullOrEmpty(version) ? "Visual Studio" : $"Visual Studio {version}")
            : $"Visual Studio {year}";
        return (name, year ?? "", version, edition);
    }

    //  Build (MCP buildSolution / buildProject)

    /// <summary>Build the whole solution (projectName null) or a single project,
    /// synchronously, then report success + the current Error List errors. The
    /// DTE build is blocking, so run it on a background-safe path: switch to the
    /// UI thread, kick the build, wait via SolutionBuild.BuildState polling.</summary>
    public async Task<BuildResult> BuildAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var sb = dte?.Solution?.SolutionBuild;
        if (sb == null)
        {
            return new BuildResult { Ok = false, Message = "No solution open." };
        }
        try
        {
            if (string.IsNullOrEmpty(projectName))
            {
                sb.Build(WaitForBuildToFinish: true);
            }
            else
            {
                // BuildProject needs the active solution-configuration name and the
                // project's unique name; match by file name or unique name.
                var config = sb.ActiveConfiguration?.Name;
                var proj = FindProject(dte, projectName);
                if (proj == null)
                {
                    return new BuildResult { Ok = false, Message = $"Project not found: {projectName}" };
                }
                sb.BuildProject(config, proj.UniqueName, WaitForBuildToFinish: true);
            }

            // LastBuildInfo = number of projects that FAILED (0 = success).
            var failed = sb.LastBuildInfo;
            var errors = await CollectBuildErrorsAsync(dte);
            return new BuildResult
            {
                Ok = failed == 0,
                FailedProjects = failed,
                Errors = errors,
                Message = failed == 0 ? "Build succeeded." : $"Build failed: {failed} project(s).",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Ide.BuildAsync", ex);
            return new BuildResult { Ok = false, Message = $"Build error: {ex.Message}" };
        }
    }

    private static Project FindProject(DTE dte, string name)
    {
        foreach (Project p in dte.Solution.Projects)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(p.UniqueName), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.UniqueName, name, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Read build errors (severity = error) from the Error List after a build.</summary>
    private static async Task<List<BuildError>> CollectBuildErrorsAsync(DTE dte)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var list = new List<BuildError>();
        var items = (dte as DTE2)?.ToolWindows?.ErrorList?.ErrorItems;
        if (items == null) { return list; }
        for (int i = 1; i <= items.Count; i++)
        {
            try
            {
                var it = items.Item(i);
                // ErrorLevel: 4 = error in EnvDTE (vsBuildErrorLevelHigh). Keep errors only.
                if (it.ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelHigh) { continue; }
                list.Add(new BuildError
                {
                    File = it.FileName,
                    Line = it.Line,
                    Description = it.Description,
                    Project = it.Project,
                });
            }
            catch { /* skip malformed item */ }
        }
        return list;
    }

    /// <summary>Set the solution's startup project (the one F5/debug_start launches).
    /// Resolves the friendly name to the DTE UniqueName; lists available projects on mismatch.</summary>
    public async Task<(bool Ok, string StartupProject, string Reason)> SetStartupProjectAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution == null) { return (false, null, "No solution open."); }
        var names = new List<string>();
        Project match = null;
        foreach (Project p in dte.Solution.Projects)
        {
            // Recurse into solution folders so nested projects are reachable.
            CollectStartupCandidate(p, projectName, names, ref match);
        }
        if (match == null)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return (false, null, "Project not found. Available: " + string.Join(", ", names));
        }
        dte.Solution.SolutionBuild.StartupProjects = match.UniqueName;
        return (true, match.Name, null);
    }

    private static void CollectStartupCandidate(Project p, string target, List<string> names, ref Project match)
    {
        if (p == null) { return; }
        if (p.Kind == SolutionFolderKind)
        {
            if (p.ProjectItems == null) { return; }
            foreach (ProjectItem item in p.ProjectItems)
            {
                if (item.SubProject != null) { CollectStartupCandidate(item.SubProject, target, names, ref match); }
            }
            return;
        }
        names.Add(p.Name);
        if (string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)) { match = p; }
    }

    //  Project structure (MCP getProjectStructure)

    /// <summary>Solution tree: each project with its name, kind, file path, and
    /// the source files it contains (recursing into folders). Lets Claude see the
    /// layout without globbing the disk. Skips solution folders' own entry but
    /// recurses their children.</summary>
    public async Task<SolutionStructure> GetProjectStructureAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var result = new SolutionStructure
        {
            SolutionPath = dte?.Solution?.FullName ?? "",
            Projects = [],
        };
        if (dte?.Solution?.Projects == null) { return result; }
        try
        {
            foreach (Project p in dte.Solution.Projects)
            {
                CollectProject(p, result.Projects);
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.GetProjectStructureAsync", ex); }
        return result;
    }

    // vsProjectKindSolutionItems / vsProjectKindMisc: a "solution folder" — it has
    // no real project, but its ProjectItems may nest sub-projects, so recurse.
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    private static void CollectProject(Project p, List<ProjectNode> into)
    {
        if (p == null) { return; }
        if (p.Kind == SolutionFolderKind)
        {
            if (p.ProjectItems == null) { return; }
            foreach (ProjectItem item in p.ProjectItems)
            {
                if (item.SubProject != null) { CollectProject(item.SubProject, into); }
            }
            return;
        }

        var node = new ProjectNode
        {
            Name = p.Name,
            Path = SafeProjectPath(p),
            Files = [],
        };
        try { CollectFiles(p.ProjectItems, node.Files); }
        catch { /* partial tree is fine */ }
        into.Add(node);
    }

    private static string SafeProjectPath(Project p)
    {
        try { return p.FullName; } catch { return ""; }
    }

    private static void CollectFiles(ProjectItems items, List<string> into)
    {
        if (items == null) { return; }
        foreach (ProjectItem item in items)
        {
            try
            {
                // A physical file: FileCount >= 1 with a real path.
                for (short i = 1; i <= item.FileCount; i++)
                {
                    var f = item.FileNames[i];
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) { into.Add(PathHelpers.LowercaseDrive(f)); }
                }
            }
            catch { /* skip */ }
            if (item.ProjectItems?.Count > 0)
            {
                CollectFiles(item.ProjectItems, into);
            }
        }
    }

    //  Workspace folders (lock file + getWorkspaceFolders)

    /// <summary>Folders that define the "workspace" for the CLI. VS
    /// doesn't have multi-root workspaces — we return the single
    /// solution folder (or empty when no solution is open, which falls
    /// back to the CLI's own CWD).</summary>
    public async Task<IReadOnlyList<string>> GetWorkspaceFoldersAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var solutionPath = dte?.Solution?.FullName;
        if (string.IsNullOrEmpty(solutionPath)) { return []; }
        var folder = Path.GetDirectoryName(solutionPath);
        return string.IsNullOrEmpty(folder)
                ? Array.Empty<string>()
                : [PathHelpers.LowercaseDrive(folder)];
    }


    //  Selection (MCP getCurrentSelection)

    /// <summary>Async snapshot of the active editor's selection for the
    /// MCP path. Returns null when no text document is active.
    /// Coordinates are 1-based to match VS conventions.</summary>
    public async Task<EditorSelection> GetCurrentSelectionAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var doc = dte?.ActiveDocument;
        return doc?.Selection is not TextSelection sel
            ? null
            : new EditorSelection
            {
                FilePath = doc.FullName,
                Text = sel.Text ?? string.Empty,
                StartLine = sel.TopPoint?.Line ?? 0,
                StartColumn = sel.TopPoint?.DisplayColumn ?? 0,
                EndLine = sel.BottomPoint?.Line ?? 0,
                EndColumn = sel.BottomPoint?.DisplayColumn ?? 0,
                IsEmpty = string.IsNullOrEmpty(sel.Text),
            };
    }

    //  Open editors (MCP getOpenEditors)

    /// <summary>List of files currently open in editor tabs (text only).
    /// Order matches VS's tab order. Each entry includes active/dirty.</summary>
    public async Task<IReadOnlyList<OpenEditor>> GetOpenEditorsAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;

        // Enumerate the shell's document-window frames: this is the authoritative
        // list of open tabs (DTE.Documents misses preview / never-clicked tabs
        // whose buffer isn't materialized). Read the path + dirty flag straight
        // off the frame so unmaterialized tabs are included.
        var activePath = dte?.ActiveDocument?.FullName;
        // Language is only available via DTE.Documents; index it for the tabs
        // that have it (best-effort enrichment).
        var langByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dte?.Documents != null)
        {
            foreach (Document d in dte.Documents)
            {
                if (!string.IsNullOrEmpty(d?.FullName)) { langByPath[d.FullName] = d.Language ?? string.Empty; }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<OpenEditor>();
        foreach (var frame in DocumentFrames())
        {
            var path = FrameMoniker(frame);
            if (string.IsNullOrEmpty(path) || !seen.Add(path)) { continue; }
            langByPath.TryGetValue(path, out var lang);
            list.Add(new OpenEditor
            {
                FilePath = path,
                IsActive = string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase),
                IsDirty = FrameDirty(frame),
                Language = lang ?? string.Empty,
            });
        }
        return list;
    }

    //  Diagnostics (MCP getDiagnostics)

    /// <summary>Read the IDE's Error List in the LSP shape the Claude CLI
    /// expects (DiagnosticFile[] — see <c>parseDiagnosticResult</c>
    /// in the CLI source). Optional URI filter restricts to one file.</summary>
    public async Task<IReadOnlyList<DiagnosticFile>> GetDiagnosticsAsync(string fileUriFilter)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var errors = (dte as DTE2)?.ToolWindows?.ErrorList?.ErrorItems;
        if (errors == null) { return []; }

        var byFile = new Dictionary<string, List<Diagnostic>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= errors.Count; i++)  // ErrorItems is 1-based
        {
            ErrorItem item;
            try { item = errors.Item(i); } catch { continue; }
            if (item == null) { continue; }
            var file = item.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(file)) { continue; }
            // VS gives 1-based line+col; LSP wants 0-based + end coords.
            var lineZero = Math.Max(0, item.Line - 1);
            var colZero = Math.Max(0, item.Column - 1);
            var diag = new Diagnostic
            {
                // HTML-decode: the Error List sometimes returns descriptions with
                // HTML entities (XAML-prepared); without decoding Claude misreads
                // e.g. `/&quot;/g` instead of `/"/g`.
                Message = System.Net.WebUtility.HtmlDecode(item.Description ?? string.Empty),
                Severity = SeverityToLsp(item.ErrorLevel),
                Range = new DiagnosticRange
                {
                    Start = new DiagnosticPosition { Line = lineZero, Character = colZero },
                    End = new DiagnosticPosition { Line = lineZero, Character = colZero + 1 },
                },
                Source = item.Project ?? null,
                Code = ExtractCode(item.Description),
            };
            if (!byFile.TryGetValue(file, out var bucket)) { byFile[file] = bucket = []; }
            bucket.Add(diag);
        }

        var result = new List<DiagnosticFile>(byFile.Count);
        foreach (var kv in byFile)
        {
            var uri = PathHelpers.ToFileUri(kv.Key);
            if (!string.IsNullOrEmpty(fileUriFilter) && !PathHelpers.UrisEquivalent(fileUriFilter, uri)) { continue; }
            result.Add(new DiagnosticFile { Uri = uri, Diagnostics = kv.Value });
        }
        // Dictionary iteration order is unspecified — sort by file for a stable result.
        result.Sort((a, b) => string.CompareOrdinal(a.Uri, b.Uri));
        return result;
    }

    private static string SeverityToLsp(vsBuildErrorLevel level) => level switch
    {
        vsBuildErrorLevel.vsBuildErrorLevelHigh => "Error",
        vsBuildErrorLevel.vsBuildErrorLevelMedium => "Warning",
        vsBuildErrorLevel.vsBuildErrorLevelLow => "Info",
        _ => "Info",
    };

    /// <summary>True if the file is open in a visible document-window frame — drives the post-edit
    /// diagnostics wait timing (visible editors settle faster than background ones). Reuses the same
    /// frame enumeration as GetOpenEditorsAsync/SaveDocumentAsync; any failure degrades to false
    /// (the caller then falls back to the slower, safe single-wait timing).</summary>
    public async Task<bool> IsFileVisible(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var target = PathHelpers.FromFileUri(filePath);
            foreach (var frame in DocumentFrames())
            {
                if (!PathEquals(FrameMoniker(frame), target)) { continue; }
                return frame.IsVisible() == VSConstants.S_OK;
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Ide.IsFileVisible", ex); }
        return false;
    }

    // The Error List has no error-code field; VS embeds it as a "CS0103: ..." prefix in the
    // description. Pull it out so diagnostics carry a stable code (empty when absent).
    private static string ExtractCode(string description)
    {
        if (string.IsNullOrEmpty(description)) { return ""; }
        var m = System.Text.RegularExpressions.Regex.Match(description, @"^([A-Za-z]{2,}\d+)\b");
        return m.Success ? m.Groups[1].Value : "";
    }

    //  Open file (MCP openFile)

    /// <summary>Open a file in the editor. Optionally selects a range
    /// matching <paramref name="startText"/> through <paramref name="endText"/>
    /// (text-pattern selection — what the CLI uses to point at code
    /// it's discussing).</summary>
    public async Task<bool> OpenFileAsync(
        string filePath, int startLine, int endLine, bool activate)
    {
        filePath = PathHelpers.FromFileUri(filePath);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE dte) { return false; }
        try
        {
            var window = dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
            if (activate) { window?.Activate(); }
            // Select whole lines. Line-based only: Claude reasons in line numbers (Read returns
            // "N: content"), so a text match would be awkward and ambiguous when a line repeats.
            if (startLine > 0 && window?.Document?.Selection is TextSelection sel)
            {
                var lastLine = endLine >= startLine ? endLine : startLine;
                sel.MoveToLineAndOffset(startLine, 1, false);
                sel.MoveToLineAndOffset(lastLine, 1, true);
                sel.EndOfLine(true);
            }
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Ide.OpenFile", ex);
            return false;
        }
    }


    //  Diff (used by both MCP openDiff and the WebView chat)

    /// <summary><para>
    /// Open a side-by-side diff between an existing file and
    /// proposed new content. Delegates to <see cref="IdeDiffViewer"/>
    /// (single implementation, used by both the MCP server and the
    /// WebView chat).
    /// </para>
    /// <para>
    /// Blocks until the user resolves the diff (Accept/Reject InfoBar,
    /// save-to-apply, or close-to-reject); see <see cref="IdeDiffViewer"/>.
    /// </para></summary>
    public Task<IdeDiffViewer.DiffResult> OpenDiffAsync(
        string oldFilePath, string newFilePath, string newFileContents, string tabName)
        => IdeDiffViewer.Instance.OpenAsync(oldFilePath, newFilePath, newFileContents, tabName);

    /// <summary>Show a diff between two strings (used by the WebView
    /// chat path: it has the original and proposed contents in memory).
    /// Wrapper around <see cref="OpenDiffAsync"/> with a synthetic
    /// <c>old</c> path written to temp.</summary>
    public Task ShowDiffAsync(string filePath, string oldContent, string newContent)
        => IdeDiffViewer.Instance.ShowFromContentsAsync(filePath, oldContent, newContent);

    /// <summary>Close the last diff opened by <see cref="ShowDiffAsync"/>.
    /// Used by the WebView chat to dismiss its preview.</summary>
    public void CloseLastDiff() => IdeDiffViewer.Instance.CloseLast();

    /// <summary>Close a specific diff tab by its <paramref name="tabName"/>.
    /// Looks up the frame in <see cref="IdeDiffViewer"/>'s open-frames
    /// registry — we never match by caption (which would risk closing
    /// one of our own panes or any other tab whose title happens to
    /// contain "Claude Code").</summary>
    public Task CloseTabAsync(string tabName)
        => IdeDiffViewer.Instance.CloseTabAsync(tabName);

    /// <summary>Close every diff frame opened by us. Iterates the
    /// open-frames registry so we close ONLY our diffs.</summary>
    public Task<int> CloseAllDiffTabsAsync()
        => IdeDiffViewer.Instance.CloseAllAsync();

    //  Format / organize (MCP formatDocument / organizeImports)

    /// <summary>Run VS's <c>Edit.FormatDocument</c> command on the given
    /// file. Identical to the user pressing Ctrl+K, Ctrl+D — respects
    /// .editorconfig, analyzer rules, and language-specific formatters.
    /// Opens the file if it isn't already to give the formatter a live
    /// document to act on.</summary>
    public Task<bool> FormatDocumentAsync(string filePath)
        => RunOnActiveDocumentAsync(filePath, "Edit.FormatDocument");

    /// <summary>Run VS's <c>Edit.RemoveAndSort</c> command (organize +
    /// remove unused usings/imports). Same caveat as
    /// <see cref="FormatDocumentAsync"/>: file is opened for the
    /// language service to do its work.</summary>
    public Task<bool> OrganizeImportsAsync(string filePath)
        => RunOnActiveDocumentAsync(filePath, "Edit.RemoveAndSort");

    /// <summary>Run VS's Code Cleanup on a file (Ctrl+K, Ctrl+E), applying the
    /// fixers of the user's default cleanup profile. The profile isn't
    /// selectable here: <c>ExecuteCommand</c> always runs the default one.
    /// Which fixers actually exist depends on the language — rich for C#/VB,
    /// little to nothing elsewhere — so success only means the command ran.</summary>
    public Task<bool> RunCleanupAsync(string filePath)
        => RunOnActiveDocumentAsync(filePath, "Edit.CodeCleanup");

    private async Task<bool> RunOnActiveDocumentAsync(string filePath, string dteCommand)
    {
        filePath = PathHelpers.FromFileUri(filePath);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE dte) { return false; }
        try
        {
            var window = dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
            window?.Activate();
            dte.ExecuteCommand(dteCommand);
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException($"Ide.{dteCommand}", ex);
            return false;
        }
    }

    //  C# Interactive (MCP executeCode)

    /// <summary>Submit a snippet to VS's C# Interactive window. MVP only
    /// opens the window; actual submission requires linking
    /// Microsoft.VisualStudio.InteractiveWindow (TODO).</summary>
    public async Task<bool> ExecuteInteractiveCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE dte) { return false; }
        try
        {
            dte.ExecuteCommand("View.C#Interactive");
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Ide.ExecuteInteractiveCode", ex);
            return false;
        }
    }
}

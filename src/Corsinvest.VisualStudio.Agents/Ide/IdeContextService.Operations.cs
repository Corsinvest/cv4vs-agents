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

    /// <summary>Wait for a build/clean kicked with WaitFor…=false, yielding the UI thread between
    /// polls.
    ///
    /// The blocking overloads (WaitForBuildToFinish: true) are a synchronous COM call that never
    /// pumps: called from the UI thread — which every DTE access has to be — they freeze the whole
    /// IDE for the length of the build. Ctrl+Shift+B doesn't, which is what gives it away as a
    /// defect rather than a constraint. The tool still can't return until the build ends (it owes
    /// the caller LastBuildInfo plus the Error List); what changes is that the user no longer waits
    /// with it.
    ///
    /// The await is what does the work: leaving the UI thread lets VS pump again, and
    /// SwitchToMainThreadAsync takes it back for the next read of BuildState, which is itself a DTE
    /// call. 200ms is short enough to not add a visible tail to a fast build and long enough to cost
    /// nothing on a long one.
    ///
    /// Returns false if BuildState never left vsBuildStateInProgress within the cap. Without one a
    /// wedged build (MSBuild zombie, a target that never closes) leaves the MCP call pending
    /// forever, and the caller has no way to tell that from a build that is merely slow.</summary>
    private static async Task<bool> WaitForBuildAsync(SolutionBuild sb)
    {
        // Poll at least once before testing: BuildState can still read vsBuildStateDone on the first
        // look after kicking off, and exiting here would report the PREVIOUS build's LastBuildInfo
        // as this one's result.
        for (var i = 0; i < BuildPollMaxTries; i++)
        {
            await Task.Delay(BuildPollMs);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (sb.BuildState != vsBuildState.vsBuildStateInProgress) { return true; }
        }
        return false;
    }

    private const int BuildPollMs = 200;
    // 30 minutes: past any real build, short enough that a wedged one still answers.
    private const int BuildPollMaxTries = 30 * 60 * 1000 / BuildPollMs;
    // 15 seconds: a cancel stops at the first target boundary, so it either lands quickly or the
    // build is wedged — waiting the full build cap would only delay saying so.
    private const int CancelPollMaxTries = 15 * 1000 / BuildPollMs;

    private static string BuildTimedOutMessage(string verb) =>
        $"{verb} still in progress after {BuildPollMaxTries * BuildPollMs / 60000} minutes — " +
        "check the Output window; the IDE was left building; build_cancel stops it.";

    /// <summary>Build the whole solution (projectName null) or a single project, then report success
    /// plus the Error List down to <paramref name="severity"/>. Kicks the build without waiting and
    /// polls SolutionBuild.BuildState, so the IDE stays live while it runs — see
    /// <see cref="WaitForBuildAsync"/>.</summary>
    public async Task<BuildResult> BuildAsync(string projectName, string severity = "error")
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
                sb.Build(WaitForBuildToFinish: false);
            }
            else
            {
                // BuildProject needs the active solution-configuration name and the
                // project's unique name; match by file name or unique name.
                var config = sb.ActiveConfiguration?.Name;
                var proj = FindProjectDeep(dte, projectName, out var available);
                if (proj == null)
                {
                    return new BuildResult
                    {
                        Ok = false,
                        Message = $"Project not found: {projectName}. Available: {string.Join(", ", available)}",
                    };
                }
                sb.BuildProject(config, proj.UniqueName, WaitForBuildToFinish: false);
            }

            if (!await WaitForBuildAsync(sb))
            {
                return new BuildResult { Ok = false, Message = BuildTimedOutMessage("Build") };
            }

            // LastBuildInfo = number of projects that FAILED (0 = success).
            var failed = sb.LastBuildInfo;
            var (errors, skipped) = await CollectBuildErrorsAsync(dte, severity);
            var message = failed == 0 ? "Build succeeded." : $"Build failed: {failed} project(s).";
            // What was filtered out gets a line of its own. Without it a green build reads as
            // "errors: [] — nothing to see", when there may be a hundred warnings behind it and no
            // hint that asking would show them.
            if (skipped > 0)
            {
                message += $" {skipped} more item(s) at a lower severity — pass severity:'warning' or 'all' to list them.";
            }
            return new BuildResult
            {
                Ok = failed == 0,
                FailedProjects = failed,
                Errors = errors,
                Message = message,
                Configuration = ActiveConfigurationName(sb),
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.BuildAsync", ex);
            return new BuildResult { Ok = false, Message = $"Build error: {ex.Message}" };
        }
    }

    /// <summary>Clean the solution (delete bin/obj outputs) and wait for it, polling like
    /// <see cref="BuildAsync"/> so the IDE stays live. No error collection: unlike a build, a clean
    /// produces no diagnostics — <c>LastBuildInfo</c> is the only outcome there is.</summary>
    public async Task<BuildResult> CleanAsync()
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
            sb.Clean(WaitForCleanToFinish: false);
            if (!await WaitForBuildAsync(sb))
            {
                return new BuildResult { Ok = false, Message = BuildTimedOutMessage("Clean") };
            }
            var failed = sb.LastBuildInfo;
            return new BuildResult
            {
                Ok = failed == 0,
                FailedProjects = failed,
                Message = failed == 0 ? "Clean succeeded." : $"Clean failed: {failed} project(s).",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.CleanAsync", ex);
            return new BuildResult { Ok = false, Message = $"Clean error: {ex.Message}" };
        }
    }

    /// <summary>Ask the running build to stop and wait for it to actually stop.
    ///
    /// The cancel goes through <c>IVsSolutionBuildManager</c>: <c>EnvDTE.SolutionBuild</c> can start,
    /// clean and query a build but has no way to stop one — there is no <c>Cancel</c> anywhere in
    /// EnvDTE, not on <c>SolutionBuild2</c> either. State is still read from <c>SolutionBuild</c>,
    /// which is what the rest of this file polls.
    ///
    /// Cancelling only requests the stop: MSBuild finishes the targets already in flight before the
    /// state leaves <c>vsBuildStateInProgress</c>, so returning right after the call would report a
    /// stop that hasn't happened. Polling here is what lets the caller learn whether the IDE is free
    /// again — otherwise the next build stacks on top of one still running.
    ///
    /// The wait is capped far below <see cref="BuildPollMaxTries"/>: a cancel that hasn't taken
    /// within seconds is a wedged build, and reporting that is more useful than blocking on it.</summary>
    public async Task<BuildResult> CancelBuildAsync()
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
            // Not an error: the caller asked for a stop and there is nothing running, which is the
            // state they wanted. Saying so plainly stops the model from retrying.
            if (sb.BuildState != vsBuildState.vsBuildStateInProgress)
            {
                return new BuildResult { Ok = true, Message = "No build is running." };
            }
            if (Package.GetGlobalService(typeof(SVsSolutionBuildManager)) is not IVsSolutionBuildManager bm)
            {
                return new BuildResult { Ok = false, Message = "Build manager not available." };
            }
            var hr = bm.CanCancelUpdateSolutionConfiguration(out var canCancel);
            if (ErrorHandler.Succeeded(hr) && canCancel == 0)
            {
                return new BuildResult
                {
                    Ok = false,
                    Message = "This build cannot be cancelled — check the Output window; the IDE was left building.",
                };
            }
            ErrorHandler.ThrowOnFailure(bm.CancelUpdateSolutionConfiguration());
            for (var i = 0; i < CancelPollMaxTries; i++)
            {
                await Task.Delay(BuildPollMs);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (sb.BuildState != vsBuildState.vsBuildStateInProgress)
                {
                    // LastBuildInfo counts projects that did not build. After a cancel that count is
                    // whatever the build had reached, not a verdict on the code — so it is reported
                    // for information and never turned into Ok=false.
                    return new BuildResult
                    {
                        Ok = true,
                        FailedProjects = sb.LastBuildInfo,
                        Message = "Build cancelled.",
                    };
                }
            }
            return new BuildResult
            {
                Ok = false,
                Message = $"Cancel requested but the build was still running {CancelPollMaxTries * BuildPollMs / 1000}s later — " +
                          "check the Output window; the IDE was left building.",
            };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.CancelBuildAsync", ex);
            return new BuildResult { Ok = false, Message = $"Cancel error: {ex.Message}" };
        }
    }

    /// <summary>Read the Error List after a build, down to <paramref name="severity"/>: "error"
    /// (the default), "warning", or "all" — each level meaning that one and everything worse, the
    /// way a log level does. Reports how many were left out, so a caller taking the default still
    /// learns there is more to ask for.</summary>
    private static async Task<(List<BuildError> items, int skipped)> CollectBuildErrorsAsync(DTE dte, string severity)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var list = new List<BuildError>();
        var skipped = 0;
        var items = (dte as DTE2)?.ToolWindows?.ErrorList?.ErrorItems;
        if (items == null) { return (list, 0); }

        // EnvDTE orders these the other way round — High(4) is an error, Low(2) an info — so the
        // floor is a minimum level, not a maximum.
        var floor = severity switch
        {
            "all" => vsBuildErrorLevel.vsBuildErrorLevelLow,
            "warning" => vsBuildErrorLevel.vsBuildErrorLevelMedium,
            _ => vsBuildErrorLevel.vsBuildErrorLevelHigh,
        };

        for (int i = 1; i <= items.Count; i++)
        {
            try
            {
                var it = items.Item(i);
                if (it.ErrorLevel < floor) { skipped++; continue; }
                list.Add(new BuildError
                {
                    File = it.FileName,
                    Line = it.Line,
                    Description = it.Description,
                    Project = it.Project,
                    Severity = SeverityToLsp(it.ErrorLevel),
                });
            }
            catch { /* skip malformed item */ }
        }
        return (list, skipped);
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Project p in dte.Solution.Projects)
        {
            // Recurse into solution folders so nested projects are reachable.
            CollectStartupCandidate(p, projectName, names, seen, 0, ref match);
        }
        if (match == null)
        {
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return (false, null, "Project not found. Available: " + string.Join(", ", names));
        }
        dte.Solution.SolutionBuild.StartupProjects = match.UniqueName;
        return (true, match.Name, null);
    }

    /// <summary>Switch the active solution configuration (Debug, Release, …), the one an
    /// argument-less build compiles. Accepts either the bare name ("Release") or the name with its
    /// platform ("Release|Any CPU"); the bare form matches the first platform offered for it.
    ///
    /// This changes the IDE for the user, not just for the call: the toolbar dropdown moves and
    /// stays moved, and their next manual build follows it. That is why it is a tool of its own
    /// rather than an argument on build_solution — a build that quietly flipped the configuration
    /// and left it there would be a side effect nobody asked for.</summary>
    public async Task<(bool Ok, string Configuration, string Reason)> SetConfigurationAsync(string configuration)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var sb = dte?.Solution?.SolutionBuild;
        if (sb == null) { return (false, null, "No solution open."); }

        try
        {
            var names = CollectConfigurations(sb, configuration, out var match);
            if (match == null)
            {
                return (false, null, "Configuration not found. Available: " + string.Join(", ", names));
            }

            match.Activate();
            return (true, ActiveConfigurationName(sb), null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.SetConfigurationAsync", ex);
            return (false, null, $"Could not switch configuration: {ex.Message}");
        }
    }

    /// <summary>Report the active solution configuration and the ones that can be asked for.
    /// Reading this through a build would mean compiling the solution to learn a setting, and
    /// finding out what exists at all would mean sending a wrong name and reading the error.</summary>
    public async Task<(bool Ok, string Configuration, string[] Available, string Reason)> GetConfigurationAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var sb = dte?.Solution?.SolutionBuild;
        if (sb == null) { return (false, null, [], "No solution open."); }

        try
        {
            var names = CollectConfigurations(sb, null, out _);
            return (true, ActiveConfigurationName(sb), [.. names], null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.GetConfigurationAsync", ex);
            return (false, null, [], $"Could not read the configuration: {ex.Message}");
        }
    }

    /// <summary>The solution's configurations as sorted "name|platform" strings, and — when
    /// <paramref name="wanted"/> is given — the one matching it, by full form or by bare name.
    ///
    /// The set is not a tidying-up: SolutionConfigurations repeats a configuration once per
    /// project that defines it, so a three-project solution offers "Debug|Any CPU" three times.
    /// Must be called on the UI thread.</summary>
    private static SortedSet<string> CollectConfigurations(SolutionBuild sb, string wanted, out SolutionConfiguration match)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        match = null;
        foreach (SolutionConfiguration c in sb.SolutionConfigurations)
        {
            // A configuration is (name, platform); SolutionContexts carries the platform, so the
            // display form is built here rather than read off a single property.
            var platform = PlatformOf(c);
            var full = string.IsNullOrEmpty(platform) ? c.Name : $"{c.Name}|{platform}";
            names.Add(full);
            if (match == null && !string.IsNullOrEmpty(wanted)
                && (string.Equals(full, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                match = c;
            }
        }
        return names;
    }

    /// <summary>Add an existing project file to the solution — Solution Explorer's "Add →
    /// Existing Project". The project file must already exist; this does not scaffold one.</summary>
    public async Task<(bool Ok, string Project, string Reason)> AddProjectToSolutionAsync(string projectPath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution == null) { return (false, null, "No solution open."); }
        if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath))
        {
            return (false, null, $"Project file not found on disk: {projectPath}");
        }

        try
        {
            var added = dte.Solution.AddFromFile(projectPath, false);
            dte.Solution.SaveAs(dte.Solution.FullName);
            return (true, added?.Name, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.AddProjectToSolutionAsync", ex);
            return (false, null, $"Could not add the project: {ex.Message}");
        }
    }

    /// <summary>Remove a project from the solution, leaving its files on disk — the project stops
    /// being built, it is not deleted.</summary>
    public async Task<(bool Ok, string Project, string Reason)> RemoveProjectFromSolutionAsync(string projectName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution == null) { return (false, null, "No solution open."); }

        var proj = FindProjectDeep(dte, projectName, out var available);
        if (proj == null)
        {
            return (false, null, $"Project not found: {projectName}. Available: {string.Join(", ", available)}");
        }

        try
        {
            var name = proj.Name;
            dte.Solution.Remove(proj);
            dte.Solution.SaveAs(dte.Solution.FullName);
            return (true, name, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.RemoveProjectFromSolutionAsync", ex);
            return (false, proj.Name, $"Could not remove the project: {ex.Message}");
        }
    }

    /// <summary>Add an existing file on disk to a project, the way Solution Explorer's "Add →
    /// Existing Item" does. Only for projects that list their files: an SDK-style project globs
    /// them in already, and is reported as such rather than being handed a duplicate item.
    ///
    /// The file must exist — this includes, it does not create. Writing the file and telling the
    /// project about it are separate steps, and merging them would give two ways to write a
    /// file.</summary>
    public async Task<(bool Ok, string Project, string Reason)> AddFileToProjectAsync(string projectName, string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution == null) { return (false, null, "No solution open."); }
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return (false, null, $"File not found on disk: {filePath}. Write the file first, then add it.");
        }

        var proj = FindProjectDeep(dte, projectName, out var available);
        if (proj == null)
        {
            return (false, null, $"Project not found: {projectName}. Available: {string.Join(", ", available)}");
        }

        try
        {
            if (proj.ProjectItems == null)
            {
                return (false, proj.Name, $"'{proj.Name}' has no item list to add to — its project type manages files itself.");
            }
            if (IsFileInProject(proj, filePath))
            {
                return (true, proj.Name, "Already in the project — nothing to do.");
            }
            proj.ProjectItems.AddFromFile(filePath);
            proj.Save();
            return (true, proj.Name, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.AddFileToProjectAsync", ex);
            return (false, proj.Name, $"Could not add the file: {ex.Message}");
        }
    }

    /// <summary>Remove a file from a project without deleting it from disk — <c>ProjectItem.Remove</c>,
    /// not <c>Delete</c>. Taking a file out of the build and destroying it are different intents, and
    /// only the first is reversible.</summary>
    public async Task<(bool Ok, string Project, string Reason)> RemoveFileFromProjectAsync(string projectName, string filePath)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        if (dte?.Solution == null) { return (false, null, "No solution open."); }

        var proj = FindProjectDeep(dte, projectName, out var available);
        if (proj == null)
        {
            return (false, null, $"Project not found: {projectName}. Available: {string.Join(", ", available)}");
        }

        try
        {
            var item = FindProjectItem(proj.ProjectItems, filePath);
            if (item == null)
            {
                return (false, proj.Name, $"'{filePath}' is not an item of '{proj.Name}'.");
            }
            item.Remove();
            proj.Save();
            return (true, proj.Name, null);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Ide.RemoveFileFromProjectAsync", ex);
            return (false, proj.Name, $"Could not remove the file: {ex.Message}");
        }
    }

    /// <summary>Find a project by name, descending into solution folders, and collect the names of
    /// all real projects for the not-found message. Must be called on the UI thread.</summary>
    private static Project FindProjectDeep(DTE dte, string name, out List<string> available)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        available = [];
        Project match = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Project p in dte.Solution.Projects)
        {
            CollectStartupCandidate(p, name, available, seen, 0, ref match);
        }
        available.Sort(StringComparer.OrdinalIgnoreCase);
        return match;
    }

    /// <summary>Whether the project already lists this file, by full path. Must be called on the
    /// UI thread.</summary>
    private static bool IsFileInProject(Project proj, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return FindProjectItem(proj.ProjectItems, filePath) != null;
    }

    /// <summary>Locate a ProjectItem by file path, recursing into folders. Compares the item's own
    /// FileNames(1) rather than its name, so a file added under a different display name is still
    /// found. Must be called on the UI thread.</summary>
    private static ProjectItem FindProjectItem(ProjectItems items, string filePath, int depth = 0)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (items == null || depth > MaxProjectDepth) { return null; }
        foreach (ProjectItem item in items)
        {
            try
            {
                for (short i = 1; i <= item.FileCount; i++)
                {
                    if (string.Equals(item.FileNames[i], filePath, StringComparison.OrdinalIgnoreCase)) { return item; }
                }
            }
            catch { /* an item that won't report its files can't be the one we want */ }

            var nested = FindProjectItem(item.ProjectItems, filePath, depth + 1);
            if (nested != null) { return nested; }
        }
        return null;
    }

    /// <summary>The platform of a solution configuration, read from its first project context —
    /// SolutionConfiguration exposes no platform of its own. Empty when it has no projects.</summary>
    private static string PlatformOf(SolutionConfiguration c)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            foreach (SolutionContext sc in c.SolutionContexts) { return sc.PlatformName ?? ""; }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[ide] could not read the platform of configuration '{c.Name}': {ex.Message}");
        }
        return "";
    }

    /// <summary>Active solution configuration as "name|platform", for reporting what a build
    /// actually compiled. Null when there is no solution or the IDE won't say.</summary>
    private static string ActiveConfigurationName(SolutionBuild sb)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var active = sb?.ActiveConfiguration;
        if (active == null) { return null; }
        var platform = PlatformOf(active);
        return string.IsNullOrEmpty(platform) ? active.Name : $"{active.Name}|{platform}";
    }

    // Walks the same solution-folder graph as CollectProject, so it carries the same cycle guard.
    private static void CollectStartupCandidate(
        Project p, string target, List<string> names, HashSet<string> seen, int depth, ref Project match)
    {
        if (p == null || depth > MaxProjectDepth) { return; }
        if (!seen.Add(ProjectIdentity(p))) { return; }
        if (p.Kind == SolutionFolderKind)
        {
            if (p.ProjectItems == null) { return; }
            foreach (ProjectItem item in p.ProjectItems)
            {
                if (item.SubProject != null)
                {
                    CollectStartupCandidate(item.SubProject, target, names, seen, depth + 1, ref match);
                }
            }
            return;
        }
        names.Add(p.Name);
        // Three spellings, because a caller has three plausible ones to hand: the display name, the
        // solution-relative UniqueName, and the project file's own name — which differs from the
        // display name whenever the .csproj was renamed in the solution but not on disk.
        if (match == null && NameMatchesProject(p, target)) { match = p; }
    }

    /// <summary>Whether a project answers to <paramref name="name"/> — by display name, by
    /// UniqueName, or by its project file name without the extension.</summary>
    private static bool NameMatchesProject(Project p, string name)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(name)) { return false; }
        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { return true; }
        try
        {
            return string.Equals(p.UniqueName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(p.UniqueName), name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // An unloaded project throws on UniqueName; the display name above already had its say.
            OutputWindowLogger.Global.Warn($"[ide] could not read UniqueName of project '{p.Name}': {ex.Message}");
            return false;
        }
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
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Project p in dte.Solution.Projects)
            {
                CollectProject(p, result.Projects, seen, 0);
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.GetProjectStructureAsync", ex); }
        // Solution enumeration order is not a stable contract — sort for a repeatable result.
        result.Projects.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // vsProjectKindSolutionItems / vsProjectKindMisc: a "solution folder" — it has
    // no real project, but its ProjectItems may nest sub-projects, so recurse.
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    // A solution folder can nest sub-projects, and nothing stops a hand-written .sln from making
    // that graph cyclic. A StackOverflowException is NOT catchable in .NET — it would take down
    // devenv.exe, while every other failure here degrades to a clean JSON-RPC error. The visited
    // set also stops a project reachable by two paths from being reported twice.
    private const int MaxProjectDepth = 32;

    private static void CollectProject(Project p, List<ProjectNode> into, HashSet<string> seen, int depth)
    {
        if (p == null || depth > MaxProjectDepth) { return; }
        if (!seen.Add(ProjectIdentity(p))) { return; }
        if (p.Kind == SolutionFolderKind)
        {
            if (p.ProjectItems == null) { return; }
            foreach (ProjectItem item in p.ProjectItems)
            {
                if (item.SubProject != null) { CollectProject(item.SubProject, into, seen, depth + 1); }
            }
            return;
        }

        var node = new ProjectNode
        {
            Name = p.Name,
            Path = SafeProjectPath(p),
            Files = [],
        };
        // A file reachable twice (linked item, an item exposing the same path through several
        // FileNames indices) would otherwise be listed twice — seen in a real solution.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { CollectFiles(p.ProjectItems, files, 0); }
        catch { /* partial tree is fine */ }
        node.Files.AddRange(files);
        // ProjectItems order follows the project file, not a contract.
        node.Files.Sort(StringComparer.OrdinalIgnoreCase);
        into.Add(node);
    }

    /// <summary>Identity for the visited set. <c>UniqueName</c> is the stable one, but it throws
    /// on projects in a transient state (unloaded, still loading) — fall back to the path, then
    /// the name, so a hiccup can't make two distinct projects collide on the same empty key.</summary>
    private static string ProjectIdentity(Project p)
    {
        try { if (!string.IsNullOrEmpty(p.UniqueName)) { return "u:" + p.UniqueName; } } catch { }
        var path = SafeProjectPath(p);
        if (!string.IsNullOrEmpty(path)) { return "p:" + path; }
        try { return "n:" + p.Name; } catch { return "?:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p); }
    }

    private static string SafeProjectPath(Project p)
    {
        try { return p.FullName; } catch { return ""; }
    }

    // Same reasoning as CollectProject, but nested items have no stable identity to dedupe on,
    // so depth is the guard. 64 is far past any real folder nesting.
    private const int MaxItemDepth = 64;

    private static void CollectFiles(ProjectItems items, HashSet<string> into, int depth)
    {
        if (items == null || depth > MaxItemDepth) { return; }
        foreach (ProjectItem item in items)
        {
            try
            {
                // A physical file: FileCount >= 1 with a real path.
                for (short i = 1; i <= item.FileCount; i++)
                {
                    var f = item.FileNames[i];
                    // Left as DTE gives it: LowercaseDrive exists for the lock file, whose
                    // workspaceFolders the CLI compares case-sensitively. Applying it here only
                    // made these paths disagree with solutionPath and the project paths in the
                    // same response, which breaks anyone comparing them as strings.
                    if (!string.IsNullOrEmpty(f) && File.Exists(f)) { into.Add(f); }
                }
            }
            catch { /* skip */ }
            if (item.ProjectItems?.Count > 0)
            {
                CollectFiles(item.ProjectItems, into, depth + 1);
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
        // The package already holds the resolved folder and keeps it current through the solution
        // events — reading DTE.Solution.FullName here would mean re-deriving it, and getting it
        // wrong wherever that property isn't a .sln path (Open Folder mode hands back the folder
        // itself, so taking its directory yields the PARENT).
        var folder = AgentsPackage.Instance?.CurrentSolutionFolder;
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

    /// <summary>List of files currently open in editor tabs (text only), sorted by path.
    /// Each entry includes active/dirty.</summary>
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
        // GetDocumentWindowEnum gives no ordering guarantee, so sort like every other list tool.
        list.Sort((a, b) => string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    //  Diagnostics (MCP getDiagnostics)

    /// <summary>Bucket for Error List entries that name no file. Deliberately not a file: URI, so
    /// nothing downstream mistakes it for a path it could open.</summary>
    private const string SolutionLevelUri = "vs-solution:diagnostics";

    /// <summary>Read the IDE's Error List in the LSP shape the Claude CLI
    /// expects (DiagnosticFile[] — see <c>parseDiagnosticResult</c>
    /// in the CLI source). Optional URI filter restricts to one file;
    /// <paramref name="severityFilter"/> ("Error"/"Warning"/"Info") drops the
    /// other levels, and <paramref name="maxResults"/> caps the diagnostics
    /// kept. The cap is applied AFTER filtering, so asking for errors under a
    /// pile of warnings still returns the errors.</summary>
    public async Task<IReadOnlyList<DiagnosticFile>> GetDiagnosticsAsync(
        string fileUriFilter, string severityFilter = null, int maxResults = 0)
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
            // No file name: a project- or solution-level entry (an unresolved reference, a project
            // that would not load a file). Dropping those lost 14 of 232 items on this solution,
            // and they are the ones a caller cannot find any other way — file-level diagnostics at
            // least surface again when that file is opened. They group under a marker URI rather
            // than an empty one, which the CLI's parseDiagnosticResult would treat as a real path.
            var file = item.FileName ?? string.Empty;
            var fileless = string.IsNullOrEmpty(file);
            if (fileless)
            {
                if (!string.IsNullOrEmpty(fileUriFilter)) { continue; }
                file = SolutionLevelUri;
            }
            var severity = SeverityToLsp(item.ErrorLevel);
            if (!string.IsNullOrEmpty(severityFilter)
                && !severity.Equals(severityFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // VS gives 1-based line+col; LSP wants 0-based + end coords.
            var lineZero = Math.Max(0, item.Line - 1);
            var colZero = Math.Max(0, item.Column - 1);
            var diag = new Diagnostic
            {
                // HTML-decode: the Error List sometimes returns descriptions with
                // HTML entities (XAML-prepared); without decoding Claude misreads
                // e.g. `/&quot;/g` instead of `/"/g`.
                Message = System.Net.WebUtility.HtmlDecode(item.Description ?? string.Empty),
                Severity = severity,
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
            var uri = kv.Key == SolutionLevelUri ? SolutionLevelUri : PathHelpers.ToFileUri(kv.Key);
            if (!string.IsNullOrEmpty(fileUriFilter) && !PathHelpers.UrisEquivalent(fileUriFilter, uri)) { continue; }
            result.Add(new DiagnosticFile { Uri = uri, Diagnostics = kv.Value });
        }
        // Dictionary iteration order is unspecified — sort by file for a stable result.
        result.Sort((a, b) => string.CompareOrdinal(a.Uri, b.Uri));
        // Cap after sorting so truncation is deterministic, and count diagnostics
        // rather than files: one file can carry hundreds on its own.
        if (maxResults > 0)
        {
            var kept = 0;
            for (int i = 0; i < result.Count; i++)
            {
                var diags = result[i].Diagnostics;
                if (kept + diags.Count <= maxResults) { kept += diags.Count; continue; }
                var room = maxResults - kept;
                if (room > 0)
                {
                    result[i] = new DiagnosticFile { Uri = result[i].Uri, Diagnostics = [.. diags.Take(room)] };
                    result.RemoveRange(i + 1, result.Count - i - 1);
                }
                else { result.RemoveRange(i, result.Count - i); }
                break;
            }
        }
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
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Ide.IsFileVisible", ex); }
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
            OutputWindowLogger.Global.LogException("Ide.OpenFile", ex);
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
    /// <c>old</c> path written to temp. <paramref name="toolUseId"/> identifies the frame so it
    /// can later be closed by request rather than by "whichever was last".</summary>
    public Task ShowDiffAsync(string toolUseId, string filePath, string oldContent, string newContent)
        => IdeDiffViewer.Instance.ShowFromContentsAsync(toolUseId, filePath, oldContent, newContent);

    /// <summary>Close the diff opened for a given tool_use, if any. Used when its permission is
    /// answered — a diff the user opened themselves is never touched.</summary>
    public void CloseDiffFor(string toolUseId) => IdeDiffViewer.Instance.CloseDiffFor(toolUseId);

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

    /// <summary>True when the path lies inside the open solution's folder. These commands rewrite
    /// the file, and File.Exists alone would let any path on disk through: a wrong guess that
    /// happens to exist — a same-named file in another repo — would be reformatted with nothing to
    /// show for it. Compares canonical paths, or <c>solution\..\..\elsewhere</c> would pass.</summary>
    private static bool IsInsideSolution(DTE dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solutionPath = dte?.Solution?.FullName;
        if (string.IsNullOrEmpty(solutionPath)) { return false; }
        var root = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrEmpty(root)) { return false; }
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullFile = Path.GetFullPath(filePath);
            return fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // A malformed path can't be shown to be inside the solution — treat it as outside.
            OutputWindowLogger.Global.LogException("Ide.IsInsideSolution", ex);
            return false;
        }
    }

    private async Task<bool> RunOnActiveDocumentAsync(string filePath, string dteCommand)
    {
        filePath = PathHelpers.FromFileUri(filePath);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (Package.GetGlobalService(typeof(DTE)) is not DTE dte) { return false; }
        if (!IsInsideSolution(dte, filePath))
        {
            OutputWindowLogger.Global.Warn($"[mcp] {dteCommand} refused: '{filePath}' is outside the open solution");
            return false;
        }
        try
        {
            var window = dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
            window?.Activate();
            dte.ExecuteCommand(dteCommand);
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"Ide.{dteCommand}", ex);
            return false;
        }
    }
}

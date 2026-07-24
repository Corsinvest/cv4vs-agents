/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Pane;
using Corsinvest.VisualStudio.Agents.Cli.Pane;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Menu;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration(AppConstants.AppName, AppConstants.AppDescription, "1.0")]
[ProvideMenuResource("Menus.ctmenu", 1)]
// Load at shell start (background) so the View → "cv4vs Agents" submenu populates its
// dynamic per-profile entries immediately — without it the package loads only on first
// pane open, and the submenu shows just the native "Claude" seed until then.
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
// Multi-instance per-session panes (each "New" spawns a fresh pane).
// Tabbed + Window=own-GUID: same-kind instances group as tabs, and Chat/CLI
// stay in SEPARATE dock areas (sharing one host mixed them up / floated CLI).
// Transient: don't persist panes across solution open-close (avoid orphans
// against a stale workdir).
[ProvideToolWindow(typeof(ChatPaneWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, Window = "e4f5a6b7-c8d9-0123-efab-de3456789012")]
[ProvideToolWindow(typeof(CliPaneWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, Window = "f5a6b7c8-d9e0-1234-fabc-ef4567890123")]
[ProvideOptionPage(typeof(AgentsGeneralPage), AppConstants.AppName, "General", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsChatPage), AppConstants.AppName, "Chat", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsDebugPage), AppConstants.AppName, "Debug", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsProfilesPage), AppConstants.AppName, "Profiles", 0, 0, true)]
// Document-tab for Statistics. A custom editor is opened by opening a file, so we map a private
// extension (.cv4vsstats) to the factory and open a placeholder file of that type from the menu.
// The file content is never read — the pane pulls live from StatsService.
[ProvideEditorFactory(typeof(Core.Stats.StatisticsEditorFactory), 0, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
[ProvideEditorExtension(typeof(Core.Stats.StatisticsEditorFactory), Core.Stats.StatisticsDocument.Extension, 50)]
[ProvideEditorLogicalView(typeof(Core.Stats.StatisticsEditorFactory), "{00000000-0000-0000-0000-000000000000}")]
[ProvideEditorFactory(typeof(Core.Usage.UsageEditorFactory), 0, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
[ProvideEditorExtension(typeof(Core.Usage.UsageEditorFactory), Core.Usage.UsageDocument.Extension, 50)]
[ProvideEditorLogicalView(typeof(Core.Usage.UsageEditorFactory), "{00000000-0000-0000-0000-000000000000}")]
[ProvideEditorFactory(typeof(Core.Context.ContextEditorFactory), 0, TrustLevel = __VSEDITORTRUSTLEVEL.ETL_AlwaysTrusted)]
[ProvideEditorExtension(typeof(Core.Context.ContextEditorFactory), Core.Context.ContextDocument.Extension, 50)]
[ProvideEditorLogicalView(typeof(Core.Context.ContextEditorFactory), "{00000000-0000-0000-0000-000000000000}")]
[Guid(PackageGuids.AgentsPackageString)]
public sealed class AgentsPackage : AsyncPackage, IVsSolutionEvents, IVsSolutionLoadEvents
{
    private uint _solutionEventsCookie;
    private IVsSolution _solution;

    static AgentsPackage() =>
        // Microsoft.Terminal.Wpf ships with VS but isn't on any VSIX probing
        // path, so resolve it from VS's own Terminal folder.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name);
            if (!string.Equals(name.Name, "Microsoft.Terminal.Wpf", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var devEnvDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (devEnvDir == null) { return null; }

            // Stable VS layout: Common7\IDE\CommonExtensions\Microsoft\Terminal\
            var basePath = Path.Combine(devEnvDir, "CommonExtensions", "Microsoft", "Terminal");
            var dllPath = Path.Combine(basePath, "Microsoft.Terminal.Wpf.dll");
            // Some Insider/Canary builds nest it one folder deeper.
            if (!File.Exists(dllPath))
            {
                dllPath = Path.Combine(basePath, "Terminal.Wpf", "Microsoft.Terminal.Wpf.dll");
            }
            return !File.Exists(dllPath) ? null : Assembly.LoadFrom(dllPath);
        };

    private static AgentsPackage _instance;

    public static AgentsPackage Instance => _instance;

    /// <summary>Folder of the open solution (null if none), cached for synchronous reads.
    /// Keeps VS's original casing — normalize via IdeContextService for lock-file / cwd matching.</summary>
    public string CurrentSolutionFolder { get; private set; }

    /// <summary>Ensure the given <c>settings.json</c> has <c>"diffTool": "auto"</c> so Claude uses
    /// the IDE diff instead of a terminal prompt. Idempotent: only fills the key if missing. Called
    /// per-pane for its profile's config-dir (see PaneWindowBase.Init), so every profile — the native
    /// "Claude" and any with a custom CLAUDE_CONFIG_DIR — gets the IDE diff activated.</summary>
    internal static void EnsureDiffToolAuto(string settingsPath)
    {
        try
        {
            Newtonsoft.Json.Linq.JObject root;
            if (File.Exists(settingsPath))
            {
                var raw = File.ReadAllText(settingsPath);
                try { root = Newtonsoft.Json.Linq.JObject.Parse(raw); }
                catch { root = []; }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                root = [];
            }
            // Don't overwrite an explicit user choice — only fill the gap.
            if (root["diffTool"] != null) { return; }
            root["diffTool"] = "auto";
            // ToIndentedString avoids JToken.ToString(Formatting), whose signature
            // shifts between Newtonsoft minor builds and would MissingMethodException
            // against VS's own Newtonsoft. See JsonExtensions.ToIndentedString.
            File.WriteAllText(settingsPath, root.ToIndentedString());
            OutputWindowLogger.Info($"Pkg: set diffTool=auto in {settingsPath}");
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Pkg.EnsureDiffToolAuto", ex); }
    }

    /// <summary>Snapshot the open panes into the current solution's workspace.json. Called on solution
    /// close/switch (OnBeforeCloseSolution); no-op without a solution.</summary>
    private static void SaveWorkspace()
    {
        var folder = Instance?.CurrentSolutionFolder;
        if (string.IsNullOrEmpty(folder)) { return; }
        Core.Workspace.WorkspaceStore.Save(folder, DateTime.Now.ToString("o"));
    }

    /// <summary>Reopen the panes saved for the current solution's workspace.json. Each saved pane is
    /// validated: an unknown profile falls back to native; a missing session .jsonl opens fresh.</summary>
    private void RestorePanesForCurrentSolution()
    {
        var folder = CurrentSolutionFolder;
        var ws = Core.Workspace.WorkspaceStore.Load(folder);
        if (ws?.Panes == null)
        {
            OutputWindowLogger.Debug(() => $"[restore] no workspace/panes for {folder ?? "<null>"}");
            return;
        }
        // Load(forEdit:false) normally includes the native "Claude" profile → profiles[0] is the fallback.
        var profiles = Core.Profiles.ProfileStore.Load(forEdit: false);
        if (profiles.Count == 0) { return; }   // defensive: nothing enabled → nothing to restore onto
        foreach (var p in ws.Panes)
        {
            var kind = string.Equals(p.Kind, "Cli", StringComparison.OrdinalIgnoreCase)
                ? Core.Panes.PaneKind.Cli : Core.Panes.PaneKind.Chat;
            var profile = profiles.FirstOrDefault(x =>
                string.Equals(x.Name, p.Profile, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
            var paths = ClaudePaths.ForProfile(profile);
            // Session .jsonl at <ClaudeFolder>/projects/<hash>/<id>.jsonl (SessionFolder = same hash the
            // CLI/session reader use). Missing → open fresh.
            string sessionId;
            if (!string.IsNullOrEmpty(p.SessionId)
                && File.Exists(Path.Combine(paths.SessionFolder(folder), p.SessionId + ".jsonl")))
            {
                sessionId = p.SessionId;
            }
            else
            {
                OutputWindowLogger.Debug(() => $"[restore] session {p.SessionId} missing on disk → opening fresh");
                sessionId = null;
            }
            PaneLauncher.OpenNew(kind, profile, resumeSessionId: sessionId);
        }
    }

    /// <summary>Restore the saved panes at shell-idle, gated on the option. Creating tool windows
    /// (FindToolWindow create:true → WebView2 init, which pumps) synchronously during package
    /// InitializeAsync, or inside the OnAfterOpenSolution COM event, freezes VS: the shell is still
    /// bringing itself up / hasn't returned from its own event. StartOnIdle defers to the UI thread
    /// once the shell has settled — the one safe moment to spawn panes on both entry paths.</summary>
    private void RestorePanesDeferred()
    {
        if (!AgentsOptions.General.RestorePanesOnSolutionOpen) { return; }
        _ = JoinableTaskFactory.StartOnIdle(() =>
        {
            try { RestorePanesForCurrentSolution(); }
            catch (Exception ex) { OutputWindowLogger.LogException("Pkg.RestorePanesDeferred", ex); }
        });
    }

    /// <summary>Re-read the solution folder from <see cref="IVsSolution"/> into <see cref="CurrentSolutionFolder"/>; call on every solution-state change.</summary>
    private void RefreshCurrentSolutionFolder()
    {
        try
        {
            if (_solution == null) { CurrentSolutionFolder = null; return; }
            _solution.GetSolutionInfo(out var dir, out _, out _);
            CurrentSolutionFolder = string.IsNullOrEmpty(dir)
                ? null
                : dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Pkg.RefreshCurrentSolutionFolder", ex);
            CurrentSolutionFolder = null;
        }
    }

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        _instance = this;
        await base.InitializeAsync(cancellationToken, progress);
        // Our data root exists from the start, so every writer/reader (profiles, stats, the
        // "open application data folder" menu) can assume it's there.
        Directory.CreateDirectory(AppPaths.DataFolder);
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        OutputWindowLogger.EnsurePaneOnUIThread();
        await ProfilesMenuCommand.InitializeAsync(this);
        await GlobalMenuCommands.InitializeAsync(this);
        // Register the Statistics document-tab editor factory (opened by the View → Statistics command).
        RegisterEditorFactory(new Core.Stats.StatisticsEditorFactory());
        RegisterEditorFactory(new Core.Usage.UsageEditorFactory());
        RegisterEditorFactory(new Core.Context.ContextEditorFactory());
        // Lazy MCP lifecycle: server runs only while >=1 session is open,
        // driven by PaneRegistry's 0->1 / ->0 transitions.
        Core.Panes.PaneRegistry.Instance.FirstSessionStarted += () => Mcp.McpServerHost.Instance.EnsureStarted();
        Core.Panes.PaneRegistry.Instance.LastSessionEnded += () => Mcp.McpServerHost.Instance.Stop();

        // Subscribe to solution events so the MCP lock file's workspaceFolders
        // stay in sync; otherwise a later `claude --ide` skips us in discovery.
        _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        _solution?.AdviseSolutionEvents(this, out _solutionEventsCookie);

        // Prime from current state: VS may already have a solution open before
        // our package activates, so we'd miss OnAfterOpenSolution.
        if (_solution?.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out var isOpenObj) == VSConstants.S_OK
            && isOpenObj is bool isOpen && isOpen)
        {
            RefreshCurrentSolutionFolder();
            // Solution was already open when we activated → OnAfterOpenSolution won't fire for it,
            // so run the pane restore here too. This is the common F5/reopen path.
            RestorePanesDeferred();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_solutionEventsCookie != 0)
            {
                _solution?.UnadviseSolutionEvents(_solutionEventsCookie);
                _solutionEventsCookie = 0;
            }
            Mcp.McpServerHost.Instance.Stop();
            AgentsOptions.Applied -= ProfilesMenuCommand.InvalidateCache;
            // Unadvise the selection sink (MS pattern: at package dispose). Dispose was never
            // called anywhere, so the IVsMonitorSelection cookie leaked for the process lifetime.
            Ide.IdeContextService.Instance.Dispose();
        }
        base.Dispose(disposing);
    }

    //  IVsSolutionEvents — only the two we actually need; everything else returns S_OK.

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        // Redundant with OnBeforeOpenSolution, but covers the rare load path
        // that skips IVsSolutionLoadEvents.
        RefreshCurrentSolutionFolder();
        // Close any panes born WITHOUT a solution (home-dir workdir): a pane belongs to the
        // context it was born in, and their workdir can't follow a solution change. On normal
        // startup the solution is already open before the user opens a pane, so the registry is
        // empty here and this is a no-op — it only fires for pre-existing home-born panes.
        try { Core.Panes.PaneRegistry.Instance.CloseAll(); }
        catch (Exception ex) { OutputWindowLogger.LogException("Pkg.OnAfterOpenSolution", ex); }
        // Reopen the panes saved for THIS solution. After CloseAll, so we start from a clean registry
        // and don't stack restored panes on top of home-born leftovers. Deferred to shell-idle (see
        // RestorePanesDeferred): spawning panes inside this COM event reenters solution state.
        RestorePanesDeferred();
        _ = Mcp.McpServerHost.Instance.RewriteLockFileAsync();
        // VS may restore editor tabs without firing a DTE event we listen to;
        // force an emit so MCP clients see the current file context now.
        try { Ide.IdeContextService.Instance.ForceEmitCurrentContext(); }
        catch (Exception ex) { OutputWindowLogger.LogException("Pkg.ForceEmit", ex); }
        return VSConstants.S_OK;
    }

    //  IVsSolutionLoadEvents — fires before IVsSolutionEvents (projects still
    //  loading), so toolbar buttons enable as soon as the solution path is known.

    int IVsSolutionLoadEvents.OnBeforeOpenSolution(string pszSolutionFilename)
    {
        // Cache the folder eagerly from the .sln path so consumers don't hit
        // IVsSolution before it's populated (projects may still be loading).
        if (!string.IsNullOrEmpty(pszSolutionFilename))
        {
            try { CurrentSolutionFolder = Path.GetDirectoryName(pszSolutionFilename); }
            catch { CurrentSolutionFolder = null; }
        }
        _ = Mcp.McpServerHost.Instance.RewriteLockFileAsync();
        return VSConstants.S_OK;
    }

    int IVsSolutionLoadEvents.OnBeforeBackgroundSolutionLoadBegins() => VSConstants.S_OK;
    int IVsSolutionLoadEvents.OnQueryBackgroundLoadProjectBatch(out bool pfShouldDelayLoadToNextIdle) { pfShouldDelayLoadToNextIdle = false; return VSConstants.S_OK; }
    int IVsSolutionLoadEvents.OnBeforeLoadProjectBatch(bool fIsBackgroundIdleBatch) => VSConstants.S_OK;
    int IVsSolutionLoadEvents.OnAfterLoadProjectBatch(bool fIsBackgroundIdleBatch) => VSConstants.S_OK;
    int IVsSolutionLoadEvents.OnAfterBackgroundSolutionLoadComplete() => VSConstants.S_OK;

    int IVsSolutionEvents.OnAfterCloseSolution(object pUnkReserved)
    {
        CurrentSolutionFolder = null;
        _ = Mcp.McpServerHost.Instance.RewriteLockFileAsync();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
    {
        // Snapshot before CloseAll drains the registry, so the closing solution's
        // workspace.json reflects the panes it actually had open.
        SaveWorkspace();
        // Panes are bound to the solution's workdir and can't follow a change,
        // so close them all here (frames still valid) to avoid orphan sessions
        // against a dead workdir. Draining the registry also stops MCP.
        try { Core.Panes.PaneRegistry.Instance.CloseAll(); }
        catch (Exception ex) { OutputWindowLogger.LogException("Pkg.OnBeforeCloseSolution", ex); }
        return VSConstants.S_OK;
    }
}

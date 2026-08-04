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
// Docked against windows VS itself owns, rather than a GUID of our own: a private GUID gives VS a
// dock area it only knows in the layout the pane was opened in, and design and debug layouts are
// kept apart — so starting the debugger left our panes with nowhere to be and closed them, while
// the built-in windows stayed put. These groups exist in every layout.
// Chat beside Solution Explorer (tall and narrow, like the tree it sits with), CLI beside Output
// (wide and short, where a terminal belongs and where VS users already look for one). That also
// keeps the two apart by their nature rather than by a GUID: an earlier attempt at sharing one
// host mixed them up and floated the CLI.
// Transient: don't persist panes across solution open-close (avoid orphans
// against a stale workdir).
[ProvideToolWindow(typeof(ChatPaneWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindSolutionExplorer)]
[ProvideToolWindow(typeof(CliPaneWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindOutput)]
[ProvideOptionPage(typeof(AgentsGeneralPage), AppConstants.AppName, "General", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsChatPage), AppConstants.AppName, "Chat", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsDebugPage), AppConstants.AppName, "Debug", 0, 0, true)]
[ProvideOptionPage(typeof(AgentsProfilesPage), AppConstants.AppName, "Profiles", 0, 0, true)]
// Statistics: a dashboard over StatsService, with no file behind it — a tool window, not a
// document. Single-instance, and NOT Transient: unlike the chat/CLI panes it holds no session and
// no working directory, so there is nothing to go stale when a solution closes.
// MDI docks it in the document area, which is where a full-width dashboard belongs (and it is the
// only style that puts a tool window there). Window is deliberately absent: MDI ignores it.
// This is the FIRST placement only — VS remembers wherever the user moves it afterwards.
[ProvideToolWindow(typeof(Core.Stats.StatisticsWindow), Style = VsDockStyle.MDI)]
[ProvideToolWindow(typeof(Core.Usage.UsageWindow), Style = VsDockStyle.MDI)]
[ProvideToolWindow(typeof(Core.Context.ContextWindow), Style = VsDockStyle.MDI)]
[Guid(PackageGuids.AgentsPackageString)]
public sealed class AgentsPackage : AsyncPackage, IVsSolutionEvents, IVsSolutionLoadEvents, IVsDebuggerEvents
{
    private uint _solutionEventsCookie;
    private IVsSolution _solution;
    private uint _debuggerEventsCookie;
    private IVsDebugger _debugger;

    // Solution reload watch. VS models a reload as close-then-open and no event tells the two apart
    // from a plain close, so the close is deferred and the panes are kept until we know which it was.
    private System.Threading.Timer _reloadWatch;
    private string _closingSolutionFolder;
    // Separate from the watch above: that one decides whether the panes live, this one only decides
    // whether they are on screen, and the two answers are needed at very different times.
    private System.Threading.Timer _hidePanesTimer;

    // Close→open gap, measured at ~850 ms on a small solution. Timed from OnAfterCloseSolution, so it
    // excludes unloading the projects and does not grow with the solution.
    private const int ReloadWatchCloseMs = 10_000;
    // Once a solution is loading, the wait covers the load itself, which does grow with the solution.
    // Long on purpose: a load that never completes must still trip the timer rather than leave panes
    // pinned to a solution that never came back.
    private const int ReloadWatchOpenMs = 120_000;

    // Hiding the panes is held back this long so a reload doesn't blink them off and on. Comfortably
    // past the ~850 ms close→open gap, and the gap is the only thing being waited on here — it is
    // timed from the close, so it doesn't grow with the solution the way the load does. On a real
    // close the panes linger for this long, which reads as part of the solution tearing down.
    private const int HidePanesDelayMs = 1_000;

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
            OutputWindowLogger.Global.Info($"Pkg: set diffTool=auto in {settingsPath}");
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.EnsureDiffToolAuto", ex); }
    }

    /// <summary>Snapshot the open panes into the current solution's workspace.json. Called on solution
    /// close/switch (OnBeforeCloseSolution); no-op without a solution.</summary>
    private static void SaveWorkspace()
    {
        var folder = Instance?.CurrentSolutionFolder;
        if (string.IsNullOrEmpty(folder)) { return; }
        Core.Workspace.WorkspaceStore.Save(folder, DateTime.Now.ToString("o"));
    }

    /// <summary>(Re)start the reload watch. Fires once; rearmed with a longer window when a solution
    /// starts loading. The callback lands on a pool thread and closing panes touches VS frames, hence
    /// the hop back to the UI thread.</summary>
    private void ArmReloadWatch(int dueMs)
    {
        if (_reloadWatch == null)
        {
            _reloadWatch = new System.Threading.Timer(_ => OnReloadWatchElapsed(), null,
                                                      dueMs, System.Threading.Timeout.Infinite);
        }
        else
        {
            _reloadWatch.Change(dueMs, System.Threading.Timeout.Infinite);
        }
        OutputWindowLogger.Global.Debug(() => $"[reload] watch armed for {dueMs} ms (folder={_closingSolutionFolder ?? "(none)"})");
    }

    private void DisarmReloadWatch()
        => _reloadWatch?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    /// <summary>Hide the panes shortly, unless a solution starts opening first. VS models a reload as
    /// close-then-open, so hiding on the close alone blinks them off and back on for the whole load —
    /// on a reload nothing was going away, and nothing should have moved.</summary>
    private void ArmHidePanes()
    {
        if (_hidePanesTimer == null)
        {
            _hidePanesTimer = new System.Threading.Timer(_ => OnHidePanesElapsed(), null,
                                                         HidePanesDelayMs, System.Threading.Timeout.Infinite);
        }
        else
        {
            _hidePanesTimer.Change(HidePanesDelayMs, System.Threading.Timeout.Infinite);
        }
    }

    private void DisarmHidePanes()
        => _hidePanesTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

    /// <summary>No solution started opening in time: this was a real close, so take the panes off
    /// screen — alive, so a later reload can still hand them back.</summary>
    private void OnHidePanesElapsed()
        => JoinableTaskFactory.RunAsync(async () =>
        {
            // Showing or hiding a frame has to happen on the UI thread; the timer lands on a pool one.
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            OutputWindowLogger.Global.Debug(() => "[reload] no solution opening — hiding panes");
            try { Core.Panes.PaneLauncher.HideExisting(); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.HidePanesOnClose", ex); }
        }).FileAndForget(nameof(AgentsPackage));

    /// <summary>No solution came back in time: the close was a real one. Close every pane, which is
    /// what OnBeforeCloseSolution used to do inline.</summary>
    private void OnReloadWatchElapsed()
        => JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            _closingSolutionFolder = null;
            OutputWindowLogger.Global.Info($"[reload] watch elapsed — closing {Core.Panes.PaneRegistry.Instance.Entries.Count} pane(s)");
            try { Core.Panes.PaneRegistry.Instance.CloseAll(); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.ReloadWatchElapsed", ex); }
        }).FileAndForget(nameof(AgentsPackage));

    /// <summary>Reopen the panes saved for the current solution's workspace.json. Each saved pane is
    /// validated: an unknown profile falls back to native; a missing session .jsonl opens fresh.</summary>
    private void RestorePanesForCurrentSolution()
    {
        var folder = CurrentSolutionFolder;
        var ws = Core.Workspace.WorkspaceStore.Load(folder);
        if (ws?.Panes == null)
        {
            OutputWindowLogger.Global.Debug(() => $"[restore] no workspace/panes for {folder ?? "(none)"}");
            return;
        }
        // Load(forEdit:false) normally includes the native "Claude" profile → profiles[0] is the fallback.
        var profiles = Core.Profiles.ProfileStore.Load(forEdit: false);
        if (profiles.Count == 0) { return; }   // defensive: nothing enabled → nothing to restore onto
        foreach (var p in ws.Panes)
        {
            // A reload leaves its panes alive: restoring them again would double every chat.
            if (!string.IsNullOrEmpty(p.SessionId)
                && Core.Panes.PaneRegistry.Instance.Entries.Any(e =>
                       string.Equals(e.ActiveSessionId, p.SessionId, StringComparison.OrdinalIgnoreCase)))
            {
                OutputWindowLogger.Global.Debug(() => $"[restore] session {p.SessionId} still open → skip");
                continue;
            }
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
                OutputWindowLogger.Global.Debug(() => $"[restore] session {p.SessionId} missing on disk → opening fresh");
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
            catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.RestorePanesDeferred", ex); }
        });
    }

    /// <summary>Debugger mode changed. VS keeps window layouts per mode, so panes opened at design
    /// time are absent from the run-time layout and look as though the debugger closed them — the
    /// frames are still there, just not shown. Bring back the ones the registry knows are open.
    ///
    /// Only ever shows what was already open: a pane the user closed stays closed, because it is
    /// not in the registry. StartOnIdle for the same reason as the solution-open restore — the
    /// shell is mid-transition and showing a frame from inside its event freezes it.</summary>
    public int OnModeChange(DBGMODE dbgmodeNew)
    {
        try
        {
            if (Core.Panes.PaneRegistry.Instance.Entries.Count == 0) { return VSConstants.S_OK; }
            _ = JoinableTaskFactory.StartOnIdle(() =>
            {
                try { Core.Panes.PaneLauncher.ShowExisting(); }
                catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.OnModeChange.Show", ex); }
            });
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Pkg.OnModeChange", ex);
        }
        return VSConstants.S_OK;
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
            OutputWindowLogger.Global.LogException("Pkg.RefreshCurrentSolutionFolder", ex);
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
        await ActiveSessionsMenuCommand.InitializeAsync(this);
        await GlobalMenuCommands.InitializeAsync(this);
        // Lazy MCP lifecycle: server runs only while >=1 session is open,
        // driven by PaneRegistry's 0->1 / ->0 transitions.
        Core.Panes.PaneRegistry.Instance.FirstSessionStarted += () => Mcp.McpServerHost.Instance.EnsureStarted();
        Core.Panes.PaneRegistry.Instance.LastSessionEnded += () =>
        {
            Mcp.McpServerHost.Instance.Stop();
            // Same 0-session boundary: the controller behind the browser's task manager is shared
            // by every pane, so it outlives any one of them but not all of them.
            Chat.Pane.ChatWebView.CloseTaskManagerOwner();
        };

        // Subscribe to solution events so the MCP lock file's workspaceFolders
        // stay in sync; otherwise a later `claude --ide` skips us in discovery.
        _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        _solution?.AdviseSolutionEvents(this, out _solutionEventsCookie);

        // VS keeps one window layout for design time and another for run time, and a pane opened
        // while writing code is simply not in the run-time one — so starting the debugger appears
        // to close it. The frames are still alive, so bring them back up on the mode change.
        _debugger = await GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger;
        _debugger?.AdviseDebuggerEvents(this, out _debuggerEventsCookie);

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
            if (_debuggerEventsCookie != 0)
            {
                _debugger?.UnadviseDebuggerEvents(_debuggerEventsCookie);
                _debuggerEventsCookie = 0;
            }
            _reloadWatch?.Dispose();
            _reloadWatch = null;
            _hidePanesTimer?.Dispose();
            _hidePanesTimer = null;
            Mcp.McpServerHost.Instance.Stop();
            AgentsOptions.Applied -= ProfilesMenuCommand.InvalidateCache;
            // Unadvise the selection sink (MS pattern: at package dispose). Dispose was never
            // called anywhere, so the IVsMonitorSelection cookie leaked for the process lifetime.
            Ide.IdeContextService.Instance.Dispose();
        }
        base.Dispose(disposing);
    }

    int IVsSolutionEvents.OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        // Redundant with OnBeforeOpenSolution, but covers the rare load path
        // that skips IVsSolutionLoadEvents.
        RefreshCurrentSolutionFolder();
        DisarmReloadWatch();
        // Also here, for the rare load path that skips OnBeforeOpenSolution: a solution is open, so
        // there is nothing left to hide for. Harmless when the timer already fired — ShowExisting
        // below puts back whatever it took off screen.
        DisarmHidePanes();
        _closingSolutionFolder = null;
        // Panes belong to the folder they were born in: their CLI runs there and can't follow a move.
        // Same folder → the session is still valid, so the pane (and its live process) stays. This is
        // what makes a reload survivable. Everything else closes, home-born panes included.
        var had = Core.Panes.PaneRegistry.Instance.Entries.Count;
        var kept = 0;
        try { kept = Core.Panes.PaneRegistry.Instance.CloseWhereWorkdirDiffers(CurrentSolutionFolder); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.OnAfterOpenSolution", ex); }
        OutputWindowLogger.Global.Info($"[reload] solution open on {CurrentSolutionFolder ?? "(none)"} — kept {kept} of {had} pane(s)");
        // Bring back what the close hid. StartOnIdle for the same reason the restore defers: showing
        // a frame from inside this COM event freezes the shell, which is still mid-transition.
        if (kept > 0)
        {
            _ = JoinableTaskFactory.StartOnIdle(() =>
            {
                try { Core.Panes.PaneLauncher.ShowExisting(); }
                catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.ShowPanesOnReload", ex); }
            });
        }
        // Reopen the panes saved for THIS solution — minus the ones a reload just kept alive (see
        // RestorePanesForCurrentSolution). Deferred to shell-idle (see RestorePanesDeferred):
        // spawning panes inside this COM event reenters solution state.
        RestorePanesDeferred();
        _ = Mcp.McpServerHost.Instance.RewriteLockFileAsync();
        // VS may restore editor tabs without firing a DTE event we listen to;
        // force an emit so MCP clients see the current file context now.
        try { Ide.IdeContextService.Instance.ForceEmitCurrentContext(); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("Pkg.ForceEmit", ex); }
        return VSConstants.S_OK;
    }

    //  IVsSolutionLoadEvents — fires before IVsSolutionEvents (projects still
    //  loading), so toolbar buttons enable as soon as the solution path is known.

    int IVsSolutionLoadEvents.OnBeforeOpenSolution(string pszSolutionFilename)
    {
        OutputWindowLogger.Global.Debug(() => $"[reload] solution opening: {pszSolutionFilename ?? "(none)"} — panes={Core.Panes.PaneRegistry.Instance.Entries.Count}");
        // Rearm rather than disarm: if this load fails or is cancelled, OnAfterOpenSolution never
        // comes and no close event follows either — the solution was already closed — so the panes
        // would stay pinned forever.
        if (_closingSolutionFolder != null) { ArmReloadWatch(ReloadWatchOpenMs); }
        // A solution is opening, so whatever closed was a reload — the panes belong on screen and
        // were never taken off it. Unconditional: opening one from a state with no solution has
        // nothing armed to cancel.
        DisarmHidePanes();
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
        OutputWindowLogger.Global.Debug(() => $"[reload] solution closed — panes={Core.Panes.PaneRegistry.Instance.Entries.Count}");
        CurrentSolutionFolder = null;
        // Out of sight shortly, so closing a solution looks like it always did — but alive, so a
        // reload can hand them back with the session intact. Deferred rather than immediate because
        // a reload arrives here too, and hiding on the spot made the panes blink off and back on.
        ArmHidePanes();
        // Armed here, not on the Before: unloading the projects happens in between and would eat the
        // window on a large solution.
        ArmReloadWatch(ReloadWatchCloseMs);
        _ = Mcp.McpServerHost.Instance.RewriteLockFileAsync();
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

    // Project-level events stay no-ops. A project reload is an unload followed by a load (the SDK
    // has no reload event of its own), but SDK-style projects absorb an edited .csproj in place and
    // never take that path — traced once and confirmed silent, so there is nothing to hook here.
    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

    int IVsSolutionEvents.OnBeforeCloseSolution(object pUnkReserved)
    {
        OutputWindowLogger.Global.Debug(() => $"[reload] solution closing: {CurrentSolutionFolder ?? "(none)"} — panes={Core.Panes.PaneRegistry.Instance.Entries.Count}");
        SaveWorkspace();
        // Last point where the folder is still known — OnAfterCloseSolution clears it. The panes are
        // NOT closed here: a reload would take the live CLI down with them, losing the turn in flight.
        _closingSolutionFolder = CurrentSolutionFolder;
        return VSConstants.S_OK;
    }
}

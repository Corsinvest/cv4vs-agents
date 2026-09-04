/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Pane;
using Corsinvest.VisualStudio.Agents.Cli.Pane;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// Spawns multi-instance session panes (CLI / Chat). The per-pane toolbar
/// and the entry-point command call <see cref="OpenNew"/> to create a fresh
/// pane. Callers work in terms of <see cref="PaneKind"/>; the Type mapping stays here.
/// </summary>
internal static class PaneLauncher
{
    private static Type WindowType(PaneKind kind)
        => kind == PaneKind.Cli ? typeof(CliPaneWindow) : typeof(ChatPaneWindow);

    /// <summary>Hide every pane the registry knows about, keeping the frames (and the CLI processes
    /// behind them) alive. Used while a closing solution might still be a reload coming back: the
    /// panes have to look gone straight away, but closing them would kill the session for good.
    /// Hide leaves the dock position untouched, so <see cref="ShowExisting"/> brings them back where
    /// they were. Main thread only — IVsWindowFrame is not free-threaded.</summary>
    public static void HideExisting()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }

        foreach (var entry in PaneRegistry.Instance.Entries.ToList())
        {
            try
            {
                if (Frame(pkg, entry) is IVsWindowFrame frame && frame.IsVisible() == VSConstants.S_OK)
                {
                    frame.Hide();
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException($"PaneLauncher.HideExisting({entry.Title})", ex);
            }
        }
    }

    /// <summary><para>
    /// Re-show the panes the registry already knows about, without creating any. VS holds a
    /// separate window layout for design time and run time, so panes opened while writing code are
    /// missing from the run-time one and look closed when the debugger starts — the frames are alive,
    /// they are simply not in that layout. Called on debugger mode changes, and after a solution
    /// reload to bring back what <see cref="HideExisting"/> put away.
    /// </para>
    /// <para>
    /// create: false throughout: a pane the user closed is gone from the registry and must stay
    /// closed, and a frame we cannot find is not one to conjure up.
    /// Main thread only — IVsWindowFrame is not free-threaded.
    /// </para></summary>
    public static void ShowExisting()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }

        foreach (var entry in PaneRegistry.Instance.Entries.ToList())
        {
            try
            {
                if (Frame(pkg, entry) is IVsWindowFrame frame && frame.IsVisible() != VSConstants.S_OK)
                {
                    frame.Show();
                }
            }
            catch (Exception ex)
            {
                // One pane failing to come back must not stop the others.
                OutputWindowLogger.Global.LogException($"PaneLauncher.ShowExisting({entry.Title})", ex);
            }
        }
    }

    /// <summary>Bring one open pane forward — what the Active sessions menu does. Unlike
    /// <see cref="ShowExisting"/> this shows even an already-visible pane, so picking a session
    /// buried under its siblings' tabs raises it. Main thread only.</summary>
    public static void Activate(PaneEntry entry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var pkg = AgentsPackage.Instance;
        if (pkg == null || entry == null) { return; }
        try
        {
            if (Frame(pkg, entry) is IVsWindowFrame frame) { ErrorHandler.ThrowOnFailure(frame.Show()); }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"PaneLauncher.Activate({entry.Title})", ex);
        }
    }

    /// <summary>The window frame hosting an open pane, or null when VS no longer has it.
    /// create: false — this only ever finds what is already there.</summary>
    private static object Frame(AgentsPackage pkg, PaneEntry entry)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return pkg.FindToolWindow(WindowType(entry.Kind), entry.PaneId, create: false)?.Frame;
    }

    /// <summary>The pane's working directory: the open solution's folder (or the open folder, in
    /// Open-Folder mode), else the user profile (so claude.exe always has a real cwd). Resolved
    /// once here, then injected into the entry and constant for the pane's life — a solution
    /// change closes the pane rather than moving it.</summary>
    private static string ResolveWorkdir()
        => AgentsPackage.Instance?.CurrentSolutionFolder
           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Per-pane-type monotonic id counter. Ids never recycle (closing
    /// a pane does not free its id); resets only on VS process restart.</summary>
    private static readonly Dictionary<Type, int> _nextPaneId = [];

    /// <summary>Find the next free instance id for the pane kind and ask VS to
    /// create + show it. Each call gives a brand-new pane. <paramref name="profile"/>
    /// is handed to the pane before it's shown — always a concrete profile (the native
    /// "Claude" included); callers pass the chosen or inherited one. When
    /// <paramref name="forkSessionId"/> is set (Chat only), the new pane opens resumed on
    /// that forked session instead of fresh, pre-filling the composer with
    /// <paramref name="initialPrompt"/> (the forked-at message). When
    /// <paramref name="resumeSessionId"/> is set (workspace restore, either kind), the new
    /// pane opens resumed on that session instead of fresh — a separate case from the fork,
    /// with no pre-filled prompt.
    /// <paramref name="initialPromptSend"/> runs that prompt once the pane loads instead of
    /// leaving it in the composer; it only means anything alongside a fresh
    /// <paramref name="initialPrompt"/>, since a fork is there to be edited.</summary>
    public static void OpenNew(PaneKind kind, Profile profile, string forkSessionId = null, string initialPrompt = null, string resumeSessionId = null, bool initialPromptSend = false)
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { OutputWindowLogger.Global.Warn("PaneLauncher: package not yet initialized"); return; }
        var paneType = WindowType(kind);

        OutputWindowLogger.Global.Debug(() => $"PaneLauncher: OpenNew({paneType.Name}) requested");
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!_nextPaneId.TryGetValue(paneType, out var startId)) { startId = 0; }
                for (int id = startId; id < startId + 1000; id++)
                {
                    var existing = pkg.FindToolWindow(paneType, id, create: false);
                    if (existing != null) { continue; }
                    _nextPaneId[paneType] = id + 1;
                    OutputWindowLogger.Global.Info($"PaneLauncher: creating {paneType.Name} #{id}");
                    var pane = pkg.FindToolWindow(paneType, id, create: true);
                    if (pane == null) { OutputWindowLogger.Global.Warn($"PaneLauncher: FindToolWindow null for {paneType.Name} #{id}"); return; }
                    // VS doesn't always sync VSFPROPID_MultiInstanceToolNum
                    // before OnToolWindowCreated — pass the id we know for sure.
                    if (pane is PaneWindowBase paneWindow)
                    {
                        // Create the entry BEFORE AssignPaneId: AssignPaneId → RegisterInstance →
                        // SetSessionCaption reads Entry.Title (built from the profile in its ctor), so
                        // the caption must see it on the first computation, not on a later refresh.
                        var entry = new PaneEntry(kind, profile, new PaneOptions(), ResolveWorkdir());
                        paneWindow.Init(entry);
                        paneWindow.AssignPaneId(id);
                    }

                    // A forked chat pane starts on the forked session (chat-only; forks aren't a CLI
                    // concept). A restored pane (workspace restore, either kind) starts on its saved
                    // session instead — a separate case from the fork, kept as its own branch.
                    var isFork = pane is ChatPaneWindow && !string.IsNullOrEmpty(forkSessionId);
                    if (isFork)
                    {
                        ((ChatPaneWindow)pane).SetStartupSession(forkSessionId, initialPrompt, sendPrompt: false);
                    }
                    else if (!string.IsNullOrEmpty(resumeSessionId))
                    {
                        if (pane is ChatPaneWindow chatPane)
                        {
                            chatPane.SetStartupSession(resumeSessionId, null, sendPrompt: false);
                        }
                        else if (pane is CliPaneWindow cliPane)
                        {
                            // LoadSession → SetSession, gated on _started: the pane isn't started yet,
                            // so it just records the id; the first Resize spawns with --resume. Forward
                            // through the window (Content is a DockPanel, not the control).
                            cliPane.LoadSession(resumeSessionId);
                        }
                    }
                    else if (pane is ChatPaneWindow freshChat && !string.IsNullOrEmpty(initialPrompt))
                    {
                        // A prompt with no session to start from — the editor context menu. Both
                        // branches above need one, so without this the prompt would be dropped.
                        freshChat.SetStartupSession(null, initialPrompt, initialPromptSend);
                    }
                    if (pane.Frame is IVsWindowFrame frame) { ErrorHandler.ThrowOnFailure(frame.Show()); }

                    // Focus a fresh CLI terminal so the user can type immediately (FocusInput is on
                    // IPaneControl → via the window's ActivatePane; Content is a DockPanel, not the control).
                    if (pane is CliPaneWindow newCli) { newCli.ActivatePane(); }
                    return;
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("PaneLauncher.OpenNew", ex);
                var inner = ex.InnerException;
                while (inner != null) { OutputWindowLogger.Global.LogException("  inner", inner); inner = inner.InnerException; }
            }
        });
    }

    // Switching to an existing pane isn't done here: VS recycles
    // MultiInstanceToolNum, so id lookup can resolve the wrong pane. The toolbar
    // switch uses the registry entry's ActivateAction (targets the live instance).
}

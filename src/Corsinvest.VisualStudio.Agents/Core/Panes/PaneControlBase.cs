/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// Shared base for the multi-instance pane controls (chat WebView / cli terminal).
/// Holds the plumbing identical to both kinds: the IsReady flag, the PaneRegistry
/// attach, the session-title mechanism, and the teardown tail. Kind-specific work
/// (transport startup, focus, theme apply) stays in the concrete controls via abstract
/// members and the DisposeCore hook. The solution↔pane relationship (closing panes on
/// solution open/close) lives in AgentsPackage, not here.
/// </summary>
public abstract class PaneControlBase : UserControl, IPaneControl
{
    // --- readiness (identical in both) ---
    public bool IsReady { get; private set; }
    public event EventHandler ReadyChanged;
    protected void SetReady(bool value)
    {
        if (IsReady == value) { return; }
        IsReady = value;
        ReadyChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- multi-instance plumbing ---
    /// <summary>The pane window this control lives in (set on attach); used to close the
    /// pane and push the caption.</summary>
    protected PaneWindowBase Pane { get; private set; }

    /// <summary>This pane's entry — created by PaneLauncher and injected via Init BEFORE the pane
    /// loads, so it is never null while the control lives (no attach/dispose gap → no NRE).
    /// Public getter to satisfy <see cref="IPaneControl.Entry"/> (toolbar/consumers read it there).</summary>
    public PaneEntry Entry { get; private set; }

    /// <summary>Inject the pane entry (called by the window right after CreateControl, before load).</summary>
    internal void Init(PaneEntry entry) => Entry = entry ?? throw new ArgumentNullException(nameof(entry));

    /// <summary>This pane's Claude paths (profile's CLAUDE_CONFIG_DIR). Entry is never null.</summary>
    protected ClaudePaths PaneClaudePaths => Entry.ClaudePaths;

    /// <summary>Register this pane's (already-created) entry once VS assigned the id.
    /// Called by AssignPaneId. The entry pre-exists (Init) — this only wires it to the frame.</summary>
    public void RegisterInstance(PaneWindowBase pane)
    {
        Pane = pane;
        Entry.PaneId = pane.PaneId;
        pane.RegisterInstance(Entry);
    }

    /// <summary>Re-push the pane caption from the entry's SeqNo. Both kinds call this from OnLoaded:
    /// RegisterInstance runs before the IVsWindowFrame is wired, so the caption set there can
    /// silently no-op.</summary>
    protected void RepushCaption() => Pane?.SetSessionCaption(Entry);

    // --- teardown template ---
    protected bool _disposed;

    /// <summary>Real teardown (IPaneControl), called once by PaneWindowBase.Dispose on frame
    /// close. Common tail here; kind-specific release in DisposeCore. Idempotent.</summary>
    public void DisposePane()
    {
        if (_disposed) { return; }
        _disposed = true;

        DisposeCore();

        PaneRegistry.Instance.Remove(Entry);
        Pane = null;
        // Entry stays assigned (readonly-in-practice): in-flight DisposeCore/async reads it
        // safely (Profile is immutable) — no null gap.
    }

    /// <summary>Kind-specific release: chat unsubs Options.Applied + editor context + disposes
    /// the client; cli disposes the ConPTY process. Theme unsub lives here too (handler name
    /// differs per kind). Runs before the base drops the registry entry.</summary>
    protected abstract void DisposeCore();

    /// <summary>VS fires Unloaded on dock-toggle / hide while the pane is still alive — real
    /// teardown is DisposePane on frame close, so this is a deliberate no-op.</summary>
    protected static void OnUnloaded(object sender, RoutedEventArgs e) { }

    // --- session title (mechanism identical to SetReady; the base owns it) ---
    /// <summary>Whether this pane exposes an editable title in the toolbar. Chat: true;
    /// CLI: false (the raw terminal doesn't surface its live session id). Default false.</summary>
    public virtual bool SupportsTitleEditing => false;

    private string _sessionTitle;
    public string SessionTitle => _sessionTitle;
    public event EventHandler SessionTitleChanged;

    /// <summary>Guarded setter: set the title and raise SessionTitleChanged only on change.
    /// CLI never calls it (title stays null), so the event simply never fires there.</summary>
    protected void SetSessionTitle(string value)
    {
        if (string.Equals(_sessionTitle, value, StringComparison.Ordinal)) { return; }
        _sessionTitle = value;
        SessionTitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persist a user-set title for the current session. Chat overrides to write the
    /// custom-title JSONL; CLI keeps the no-op (no editable title).</summary>
    public virtual void RenameSession(string newTitle) { }

    /// <summary>Read-only session info (id, session file, workdir, CLI) for debug and bug reports.
    /// Built entirely from <see cref="Entry"/>, which both pane kinds keep current — so the terminal
    /// gets the same dialog as the chat, with no duplicated code.</summary>
    public void ShowSessionInfo()
    {
        try
        {
            var paths = ClaudePaths.ForProfile(Entry.Profile);
            var wd = Entry.WorkingDirectory;
            var sid = Entry.ActiveSessionId;
            // "Session file", not "transcript": that's the CLI's own word for the .jsonl and it
            // means nothing to whoever opens this dialog.
            var sessionFile = !string.IsNullOrEmpty(wd) && !string.IsNullOrEmpty(sid)
                ? Path.Combine(paths.SessionFolder(wd), sid + ".jsonl")
                : "(none)";

            var info = string.Join("\n",
                $"Session title:  {(string.IsNullOrEmpty(SessionTitle) ? "(untitled)" : SessionTitle)}",
                $"Session ID:     {sid ?? "(none)"}",
                $"Session file:   {sessionFile}",
                $"Workdir:        {wd ?? "(none)"}",
                $"Profile:        {Entry.Profile?.Name ?? "(native)"}",
                $"CLI path:       {ClaudeInstall.ResolveExecutable() ?? "(not found)"}",
                $"CLI version:    {ClaudeInstall.Version() ?? "(unknown)"}");

            new DevInfoDialog(info).ShowDialog();
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Pane.ShowSessionInfo", ex); }
    }

    // --- IPaneControl kind-specific (implemented by the concrete controls) ---
    public abstract void NewSession();
    public abstract void LoadSession(string sessionId);
    public abstract void FocusInput();
    public abstract void BlurInput();

    public abstract IEnumerable<ButtonAction> MoreMenuActions { get; }
}

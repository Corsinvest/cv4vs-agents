/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// A kind-specific action for a pane's toolbar "More" menu. UI-agnostic:
/// <see cref="IconId"/> is a logical name the toolbar maps to a VS image
/// moniker, so Core/Panes stays free of VS.Imaging.
/// </summary>
public sealed class ButtonAction(string label, Action invoke, string iconId = null)
{
    public string Label { get; } = label;
    public Action Invoke { get; } = invoke;
    public string IconId { get; } = iconId;
}

/// <summary>
/// Implemented by every session pane (chat WebView, cli terminal) so the shared
/// toolbar can drive them polymorphically. Common actions are typed methods;
/// kind-specific extras flow through <see cref="MoreMenuActions"/>.
/// </summary>
public interface IPaneControl
{
    /// <summary>The pane's entry (kind, profile, workdir, session, title). Single source read by
    /// the toolbar/consumers instead of per-control pass-throughs.</summary>
    PaneEntry Entry { get; }

    /// <summary>True once the pane's backing process is running (CLI spawned /
    /// chat client initialized). The toolbar disables per-pane actions
    /// (New session here, History) until then. Raises <see cref="ReadyChanged"/>.</summary>
    bool IsReady { get; }

    /// <summary>Fires (on the UI thread) when <see cref="IsReady"/> changes.</summary>
    event EventHandler ReadyChanged;

    /// <summary>Whether this pane exposes an editable session title in the toolbar.
    /// Chat: true. CLI: false — the interactive terminal doesn't surface its live
    /// session id, so there's no title to track/rename.</summary>
    bool SupportsTitleEditing { get; }

    /// <summary>Title of the session currently loaded in this pane (custom/ai
    /// title, else the session's prompt). Null/empty when none. Shown in the
    /// toolbar; editing it renames the session.</summary>
    string SessionTitle { get; }

    /// <summary>Fires (on the UI thread) when <see cref="SessionTitle"/> changes
    /// — e.g. after loading/switching a session or an AI title arriving.</summary>
    event EventHandler SessionTitleChanged;

    /// <summary>Persist a user-set title for the current session (custom-title in
    /// the JSONL) and refresh <see cref="SessionTitle"/>. No-op if no session.</summary>
    void RenameSession(string newTitle);

    /// <summary>Start a FRESH conversation in THIS pane (not a new pane).
    /// Chat: ClaudeClient.NewSessionAsync; CLI: respawn with no --resume.</summary>
    void NewSession();

    /// <summary>Resume a past session (by id) in THIS pane.
    /// Chat: ResumeSessionAsync; CLI: respawn with --resume id.</summary>
    void LoadSession(string sessionId);

    /// <summary>Register this pane's entry in the registry once VS assigned its id.</summary>
    void RegisterInstance(PaneWindowBase pane);

    /// <summary>Focus where the user types (CLI terminal / Chat prompt box).
    /// Called after switching/loading a session.</summary>
    void FocusInput();

    /// <summary>Drop input focus (dual of <see cref="FocusInput"/>). Called when the pane loses
    /// the active VS frame. Chat blurs its WebView textarea; CLI has no DOM caret (no-op).</summary>
    void BlurInput();

    /// <summary>Extra, kind-specific actions for the "More" (⋯) menu. Chat
    /// adds WebView DevTools; CLI returns none. Empty by default.</summary>
    IEnumerable<ButtonAction> MoreMenuActions { get; }

    /// <summary>Show this pane's session info (id, session file, workdir, CLI) for debug and bug
    /// reports. Same dialog for both kinds — the base builds it from the pane's Entry.</summary>
    void ShowSessionInfo();

    /// <summary>Real teardown when the pane's frame closes for good: drop the
    /// registry entry and release resources (CLI: kill ConPTY; Chat: dispose
    /// ClaudeClient). Called once by <see cref="PaneWindowBase.Dispose(bool)"/>,
    /// NOT on WPF Unloaded (VS fires that on dock-toggle/hide while the pane is
    /// still alive). Must be idempotent.</summary>
    void DisposePane();
}

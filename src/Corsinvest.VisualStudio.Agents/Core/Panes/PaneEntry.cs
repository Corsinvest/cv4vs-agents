/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>Per-session options, living on the pane entry.</summary>
public sealed class PaneOptions
{
    /// <summary>When false, the chat's IDE-context "eye" is closed: the host stops forwarding
    /// the editor selection to this chat. Default: on (matches the VS Code extension).</summary>
    public bool SendSelection { get; set; } = true;
}

/// <summary>Which kind of pane. Lets the single <see cref="PaneRegistry"/> hold both
/// and the toolbar filter by kind.</summary>
public enum PaneKind
{
    Cli,
    Chat,
}

/// <summary>
/// The data of one live pane (CLI or Chat). Created by PaneLauncher BEFORE the window, so the
/// hosting control holds it readonly and non-null — no attach/dispose gap, no NRE reading the
/// profile. Sealed (no kind-specific subclass): nothing differs between CLI and Chat entries.
/// </summary>
public sealed class PaneEntry
{
    public PaneEntry(PaneKind kind, Profile profile, PaneOptions options, string workingDirectory)
    {
        Kind = kind;
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        SeqNo = NextSeq();
        Title = $"{(kind == PaneKind.Cli ? "CLI" : "Chat")} {SeqNo} ({profile.Name})";
        ClaudePaths = ClaudePaths.ForProfile(profile);
    }

    /// <summary>Which kind this pane is (single source — the control no longer duplicates it).</summary>
    public PaneKind Kind { get; }

    /// <summary>The pane's environment profile — ALWAYS set (native "Claude" included). Single source
    /// for the pane's provider identity: the caption/title and the Claude paths (MCP lock, sessions,
    /// stats) all derive from it.</summary>
    public Profile Profile { get; }

    /// <summary>Per-session options (the IDE-context "eye").</summary>
    public PaneOptions Options { get; }

    /// <summary>Global, ever-increasing display number across BOTH kinds; assigned once, never reused.</summary>
    public int SeqNo { get; }

    /// <summary>Multi-instance id VS assigned to the hosting pane (0, 1, 2, …). 0 until AssignPaneId;
    /// the profile-dependent paths never need it, so the gap is harmless.</summary>
    public int PaneId { get; internal set; }

    /// <summary>The pane's working directory (solution folder, else the user profile). Constant for
    /// the pane's life: a solution change closes the pane (CloseAll) rather than moving it, so this is
    /// resolved once in PaneLauncher and injected here — the single source every reader uses.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Id of the session currently attached (null = fresh). Drives the History picker's ✓.</summary>
    public string ActiveSessionId { get; internal set; }

    /// <summary>Invoked by the toolbar ✕ to close this pane. Set by the window on register.</summary>
    internal Action CloseAction { get; set; }

    /// <summary>Invoked to bring this pane to front (toolbar open-panes switch). Set by the window.</summary>
    internal Action ActivateAction { get; set; }

    /// <summary>Opens this pane's session picker — the toolbar's History popup. Set by the window,
    /// so the chat's "Resume conversation" command reaches the same UI as the toolbar button.</summary>
    internal Action ShowHistoryAction { get; set; }

    /// <summary>Dismisses the session picker if it's open, and reports whether it was. Lets the
    /// pane's Esc handler give the popup priority instead of forwarding Esc to the WebView —
    /// VS routes Esc through IOleCommandTarget, so it never reaches the popup on its own.</summary>
    internal Func<bool> DismissHistoryAction { get; set; }

    /// <summary>The single-source display title, used by BOTH the pane caption and the toolbar's
    /// open-panes list: e.g. "Chat 3 (Claude)". Computed once in the ctor (all inputs immutable).
    /// Profile.Name is always non-empty — ProfileStore.Load(forEdit:false) filters out blank-named profiles,
    /// and the native profile is named "Claude".</summary>
    public string Title { get; }

    /// <summary>This pane's Claude paths, honouring the profile's CLAUDE_CONFIG_DIR. Built once in the
    /// ctor (immutable profile): the pane reads it on every MCP lock / session / stats access.</summary>
    public ClaudePaths ClaudePaths { get; }

    private static int _seqCounter;
    private static int NextSeq() => Interlocked.Increment(ref _seqCounter);
}

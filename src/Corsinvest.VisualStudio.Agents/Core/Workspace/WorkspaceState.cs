/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Workspace;

/// <summary>Persisted per-solution workspace: which panes were open, in order. Extensible — future
/// per-solution state (pins, layout) adds sibling properties without breaking old files (see Version).</summary>
public sealed class WorkspaceState
{
    public int Version { get; set; } = 1;
    public string SavedAt { get; set; }
    public List<PaneState> Panes { get; set; } = [];
}

/// <summary>One saved pane. Identity is the array position (order = reopen order). Profile is the
/// profile NAME (ProfileStore key); SessionId is the session to --resume (null = fresh).</summary>
public sealed class PaneState
{
    public string Kind { get; set; }        // "Chat" | "Cli"
    public string Profile { get; set; }
    public string SessionId { get; set; }
}

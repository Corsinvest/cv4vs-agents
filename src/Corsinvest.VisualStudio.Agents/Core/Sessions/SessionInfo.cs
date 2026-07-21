/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents.Core.Sessions;

public class SessionInfo
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string CustomTitle { get; set; }
    public string AiTitle { get; set; }
    public string LastPrompt { get; set; }
    public string WorkingDirectory { get; set; }
    public DateTime LastUsedAt { get; set; }
    /// <summary>Permission mode from the latest user message in the JSONL; null = not found
    /// (caller falls back to "default"). Keep null: TryProcessHistoryLine's reverse-read uses
    /// it as the "still searching" sentinel.</summary>
    public string PermissionMode { get; set; }
    public string GitBranch { get; set; }
    public string CliVersion { get; set; }
    public string Model { get; set; }
    public int MessageCount { get; set; }
    /// <summary>True if any scanned line is a sub-agent sidechain entry — such
    /// sessions aren't shown in the list. Internal scan state, not serialized.</summary>
    internal bool IsSidechain { get; set; }
}

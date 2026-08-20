/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

public sealed class ClientOptions
{
    public string WorkingDirectory { get; set; }
    public string ResumeSessionId { get; set; }

    public string InitialPermissionMode { get; set; } = PermissionMode.Default;

    /// <summary>Whether this pane may ENTER <c>bypassPermissions</c> later (Options → Chat).
    /// Distinct from starting in it: the CLI refuses <c>set_permission_mode bypassPermissions</c>
    /// unless the process was launched with the allow- flag, so the capability has to be granted
    /// up front — see the launch args in <see cref="ClaudeClient"/>.</summary>
    public bool AllowBypassPermissions { get; set; }

    /// <summary>Whether the CLI should snapshot files before editing them, so Rewind has something
    /// to restore (Options → Chat).
    /// <para>Decided at launch and not changeable after: the CLI reads it from the environment when
    /// the process starts. Turning it off mid-session would leave the snapshots already taken
    /// in place, which is why the option says it applies to the next chat.</para></summary>
    public bool FileCheckpoints { get; set; } = true;

    /// <summary>Loopback port of the IDE's MCP server. When &gt; 0 it is passed as
    /// <c>CLAUDE_CODE_SSE_PORT</c> so the CLI connects to THIS server instead of
    /// scanning the lock dir — deterministic with multiple VS instances open.</summary>
    public int SsePort { get; set; }

    /// <summary>The pane's environment profile Env (null = none, native Claude). Merged into
    /// the transport's env in <see cref="ClaudeClient"/>, with our required keys applied after
    /// so a profile can never override them.</summary>
    public IReadOnlyDictionary<string, string> Env { get; set; }
}

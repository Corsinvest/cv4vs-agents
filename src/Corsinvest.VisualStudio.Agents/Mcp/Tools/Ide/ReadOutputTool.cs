/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ReadOutputArgs
{
    [Description("Output pane name. The built-in ones — 'Build', 'Debug', 'General', 'Build Order' " +
        "— work under those English names on any IDE language. Omit to list the available panes.")]
    public string Pane { get; set; }

    [Description("Max number of lines to return from the end of the pane (default 200).")]
    public int TailLines { get; set; } = 200;

    [Description("Optional regex (case-insensitive). Keeps only the lines that match, before tailLines is applied — so this is 'the last N matching lines', not 'the matches among the last N'.")]
    public string Pattern { get; set; }
}

/// <summary>MCP tool: read text from a VS Output window pane (Build, Debug, the running
/// program's output, …). Works in any mode. Omit the pane to list available panes.</summary>
internal sealed class ReadOutputTool : McpTool<ReadOutputArgs>
{
    public override string Name => "ide_read_output";
    public override string Description =>
        "Read text from a Visual Studio Output window pane (e.g. 'Build', 'Debug', or the " +
        "running program's output). Omit 'pane' to list the available pane names first — note " +
        "those come back in the IDE's language ('Compilazione' for Build on an Italian VS), but " +
        "the built-in panes are also reachable under their English names. " +
        "'tailLines' caps how many lines are returned from the end (default 200), and 'pattern' — a " +
        "case-insensitive regex — keeps only matching lines, which is how a specific message is " +
        "found in a long build log without pulling all of it; matchedLines says how many there were. " +
        "Useful to " +
        "see build/debug output or the debuggee's console writes that don't go through the " +
        "shell — including what a frozen thread stopped printing, which is how debug_freeze_thread " +
        "is checked. ide_clear_output first to read only what happens next, ide_activate_output to " +
        "put a pane in front of the user.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ReadOutputArgs args)
    {
        var r = await IdeOutputService.Instance.ReadAsync(args.Pane, args.TailLines, args.Pattern);
        if (!r.Ok) { return new { ok = false, reason = r.Reason, availablePanes = r.AvailablePanes }; }
        if (string.IsNullOrWhiteSpace(args.Pane))
        {
            return new { ok = true, availablePanes = r.AvailablePanes };
        }
        // 'pane' is the IDE's own name for it, which on a non-English install is not the name that
        // was asked for — 'Build' comes back as 'Compilazione'. Saying which name was requested
        // turns that from a mismatch worth double-checking into an answered question. Omitted
        // rather than sent as null when the two agree: a null reads as "there is an answer here
        // and it is nothing", which is not what an unremarkable name means.
        if (!string.Equals(r.Pane, args.Pane, StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                ok = true,
                pane = r.Pane,
                requestedPane = args.Pane,
                content = r.Content,
                totalLines = r.TotalLines,
                matchedLines = r.MatchedLines,
                truncated = r.Truncated,
            };
        }
        return new
        {
            ok = true,
            pane = r.Pane,
            content = r.Content,
            totalLines = r.TotalLines,
            matchedLines = r.MatchedLines,
            truncated = r.Truncated,
        };
    }
}

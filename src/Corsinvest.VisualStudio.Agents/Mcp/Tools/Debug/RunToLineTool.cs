/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RunToLineArgs
{
    [Required, Description("Absolute path of the file to run to.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line to run to. Must be executable code — a blank line or a " +
        "comment has nothing to stop on.")]
    public int Line { get; set; }
}

/// <summary>MCP tool: resume until execution reaches a line, then pause there ("Run to Cursor").
/// </summary>
internal sealed class RunToLineTool : McpTool<RunToLineArgs>
{
    public override string Name => "debug_run_to_line";
    public override string Description =>
        "Resume the paused program and stop again when it reaches this line — the Run to Cursor " +
        "command. Saves stepping through a loop or a long method one statement at a time. " +
        "Non-blocking: poll debug_get_state, and check where it actually stopped, because " +
        "anything on the way there — another breakpoint, a thrown exception — pauses it first. " +
        "If the line is never reached the program simply runs on. Leaves no breakpoint behind " +
        "either way. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(RunToLineArgs args)
    {
        var r = await IdeDebugService.Instance.RunToLineAsync(args.FilePath, args.Line);
        return new { ok = r.Ok, reason = r.Reason };
    }
}

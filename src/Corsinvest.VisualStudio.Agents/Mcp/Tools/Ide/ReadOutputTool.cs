/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ReadOutputArgs
{
    [Description("Output pane name, e.g. 'Build', 'Debug'. Omit to list the available panes.")]
    public string Pane { get; set; }

    [Description("Max number of lines to return from the end of the pane (default 200).")]
    public int TailLines { get; set; } = 200;
}

/// <summary>MCP tool: read text from a VS Output window pane (Build, Debug, the running
/// program's output, …). Works in any mode. Omit the pane to list available panes.</summary>
internal sealed class ReadOutputTool : McpTool<ReadOutputArgs>
{
    public override string Name => "ide_read_output";
    public override string Description =>
        "Read text from a Visual Studio Output window pane (e.g. 'Build', 'Debug', or the " +
        "running program's output). Omit 'pane' to list the available pane names first. " +
        "'tailLines' caps how many lines are returned from the end (default 200). Useful to " +
        "see build/debug output or the debuggee's console writes that don't go through the shell.";

    protected override async Task<object> InvokeAsync(ReadOutputArgs args)
    {
        var r = await IdeOutputService.Instance.ReadAsync(args.Pane, args.TailLines);
        if (!r.Ok) { return new { ok = false, reason = r.Reason, availablePanes = r.AvailablePanes }; }
        if (string.IsNullOrWhiteSpace(args.Pane))
        {
            return new { ok = true, availablePanes = r.AvailablePanes };
        }
        return new
        {
            ok = true,
            pane = r.Pane,
            content = r.Content,
            totalLines = r.TotalLines,
            truncated = r.Truncated,
        };
    }
}

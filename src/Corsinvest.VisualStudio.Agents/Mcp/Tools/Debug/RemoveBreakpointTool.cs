/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RemoveBreakpointArgs
{
    [Required, Description("Path to the file the breakpoint is on.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number of the breakpoint to remove.")]
    public int Line { get; set; }
}

/// <summary>MCP tool: remove the breakpoint(s) at a file/line.</summary>
internal sealed class RemoveBreakpointTool : McpTool<RemoveBreakpointArgs>
{
    public override string Name => "debug_remove_breakpoint";
    public override string Description =>
        "Remove the breakpoint(s) at a file and 1-based line. Use debug_clear_breakpoints to remove all.";

    protected override async Task<object> InvokeAsync(RemoveBreakpointArgs args)
    {
        var r = await IdeDebugService.Instance.RemoveBreakpointAsync(args.FilePath, args.Line);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

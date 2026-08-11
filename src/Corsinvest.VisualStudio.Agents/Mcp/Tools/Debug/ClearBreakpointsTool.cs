/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: remove all breakpoints in the solution.</summary>
internal sealed class ClearBreakpointsTool : McpTool<NoArgs>
{
    public override string Name => "debug_clear_breakpoints";
    public override string Description =>
        "Remove all breakpoints in the solution — every one, including any the user set " +
        "themselves, so prefer debug_remove_breakpoint when you only mean to undo your own. " +
        "debug_list_breakpoints shows what is there first. Works in any mode.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ClearBreakpointsAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

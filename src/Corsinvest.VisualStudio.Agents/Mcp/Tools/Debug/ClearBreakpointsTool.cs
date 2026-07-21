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
    public override string Description => "Remove all breakpoints in the solution.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ClearBreakpointsAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

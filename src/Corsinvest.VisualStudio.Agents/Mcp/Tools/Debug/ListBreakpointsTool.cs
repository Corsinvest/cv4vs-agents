/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: list all breakpoints currently set in the solution.</summary>
internal sealed class ListBreakpointsTool : McpTool<NoArgs>
{
    public override string Name => "debug_list_breakpoints";
    public override string Description =>
        "List all breakpoints in the solution: each with its file+line (or function name), " +
        "condition (if any), and whether it's enabled.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ListBreakpointsAsync();
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            breakpoints = r.Breakpoints.Select(b => new
            {
                file = b.File,
                line = b.Line,
                function = b.Function,
                condition = b.Condition,
                enabled = b.Enabled,
            }).ToArray(),
        };
    }
}

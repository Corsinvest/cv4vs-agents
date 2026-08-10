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
        "condition (if any), and whether it's enabled. Set them with debug_set_breakpoint or " +
        "debug_set_function_breakpoint, remove one with debug_remove_breakpoint or all with " +
        "debug_clear_breakpoints. Worth a look when a run stops somewhere unexpected — a " +
        "breakpoint left from earlier is the usual reason.";

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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: pause the running program now (Break All), without waiting for a
/// breakpoint. After it, debug_get_state reports 'break' and live inspection works.</summary>
internal sealed class BreakDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_break";
    public override string Description =>
        "Pause the running program immediately (Debug > Break All), without waiting for a " +
        "breakpoint. Only valid while running. After this the debugger is in 'break' mode, so " +
        "you can inspect the call stack and variables.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.BreakAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

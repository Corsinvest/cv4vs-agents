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
        "breakpoint, so the call stack and variables can be inspected. Non-blocking, so mode comes " +
        "back null rather than a guess: poll debug_get_state to see it reach 'break' and learn " +
        "where it stopped. Calling it on an already-paused program succeeds and says so, rather " +
        "than failing — there is nothing to do. Needs a session: debug_start first.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.BreakAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

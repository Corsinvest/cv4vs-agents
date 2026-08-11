/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: start debugging the startup project (like F5). Non-blocking — the program
/// then runs until it hits a breakpoint or ends. Use debug_get_state to know when it pauses.</summary>
internal sealed class StartDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_start";
    public override string Description =>
        "The debug entry point: the usual cycle is debug_set_breakpoint → debug_start → poll " +
        "debug_get_state until mode='break' → debug_get_callstack / debug_get_locals → debug_step. " +
        "Inspection only works in break mode, and reads the frame debug_select_frame chose on the " +
        "thread debug_select_thread chose. " +
        "Start debugging the solution's startup project (equivalent to F5). Non-blocking: " +
        "returns once launched; the program then runs until it hits a breakpoint or exits. " +
        "Poll debug_get_state to detect when it pauses (mode='break'). No-op if already debugging.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.StartAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: Start Without Debugging (Ctrl+F5) — run the program with no debugger
/// attached. Complements debug_start (which attaches the debugger).</summary>
internal sealed class StartNoDebuggerTool : McpTool<NoArgs>
{
    public override string Name => "debug_start_no_debugger";
    public override string Description =>
        "Run the solution's startup project WITHOUT the debugger (equivalent to Ctrl+F5): " +
        "breakpoints are ignored and exceptions do not break into the IDE, so none of the " +
        "debug_get_* or debug_step tools will have anything to report afterwards — use " +
        "debug_start when you need any of that. To run a different project, " +
        "solution_set_startup_project first. Returns ok or ok=false with a reason.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.StartWithoutDebuggingAsync();
        return r.Ok ? (object)new { ok = true } : new { ok = false, reason = r.Reason };
    }
}

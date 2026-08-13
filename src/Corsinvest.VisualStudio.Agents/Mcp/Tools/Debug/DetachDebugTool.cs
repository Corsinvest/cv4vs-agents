/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: detach the debugger and leave the program running — the half of "stop
/// debugging" that does not kill the process.</summary>
internal sealed class DetachDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_detach";
    public override string Description =>
        "Detach the debugger from every process it is debugging, leaving them RUNNING (Debug ▸ " +
        "Detach All). Use this after debug_attach on something you did not launch — a service, a " +
        "browser, a long-running host — where debug_stop would terminate it instead. Breakpoints " +
        "stop being hit and the debug_* inspection tools have nothing to report once detached. " +
        "Poll debug_get_state for the mode: the transition is not immediate.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.DetachAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

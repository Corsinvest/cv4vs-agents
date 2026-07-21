/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: stop the current debug session (like Shift+F5).</summary>
internal sealed class StopDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_stop";
    public override string Description =>
        "Stop the current debug session (equivalent to Shift+F5). No-op if not debugging.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.StopAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

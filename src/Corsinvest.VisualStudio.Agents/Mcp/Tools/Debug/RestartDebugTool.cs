/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: restart the current debug session (stop then start).</summary>
internal sealed class RestartDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_restart";
    public override string Description =>
        "Restart the current debug session (stop, then start again — like Debug > Restart). " +
        "If not debugging, just starts.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.RestartAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

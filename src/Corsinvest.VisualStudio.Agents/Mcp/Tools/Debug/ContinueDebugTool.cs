/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: resume execution from a paused (break) state, until the next breakpoint or
/// the program ends.</summary>
internal sealed class ContinueDebugTool : McpTool<NoArgs>
{
    public override string Name => "debug_continue";
    public override string Description =>
        "Resume execution from a paused (break) state (like F5 while paused). The program runs " +
        "until the next breakpoint or it exits. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ContinueAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

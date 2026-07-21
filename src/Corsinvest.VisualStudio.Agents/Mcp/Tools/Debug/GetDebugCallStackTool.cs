/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: get the call stack of the current thread while paused at a breakpoint.</summary>
internal sealed class GetDebugCallStackTool : McpTool<NoArgs>
{
    public override string Name => "debug_get_callstack";
    public override string Description =>
        "Get the call stack of the current thread while paused (break mode): each frame's " +
        "function, module, and (for the top frame) file/line. Only valid in break mode — if " +
        "the program is still running, poll debug_get_state until mode='break'.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.GetCallStackAsync();
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            frames = r.Frames.Select(f => new
            {
                function = f.Function,
                module = f.Module,
                file = f.File,
                line = f.Line,
            }).ToArray(),
        };
    }
}

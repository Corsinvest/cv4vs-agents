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
        "Get the call stack of the selected thread while paused (break mode): each frame's index, " +
        "function, module, and its own file/line where the frame has source. Index 0 is where " +
        "execution is paused; isCurrent marks the frame debug_get_locals and debug_evaluate read, " +
        "which debug_select_frame moves. This is one thread's stack: debug_list_threads shows the " +
        "others, debug_select_thread switches. " +
        "Only valid in break mode — if the program is still running, poll debug_get_state until " +
        "mode='break'.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.GetCallStackAsync();
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            frames = r.Frames.Select(f => new
            {
                index = f.Index,
                function = f.Function,
                module = f.Module,
                file = f.File,
                line = f.Line,
                isCurrent = f.IsCurrent,
            }).ToArray(),
        };
    }
}

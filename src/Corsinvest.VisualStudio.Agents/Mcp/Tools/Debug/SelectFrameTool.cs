/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SelectFrameArgs
{
    [Required, Description("Frame index from debug_get_callstack: 0 is where execution is paused, " +
        "counting outwards to the caller.")]
    public int Index { get; set; }
}

/// <summary>MCP tool: move the inspection tools to another frame of the call stack, so a caller's
/// locals come into scope.</summary>
internal sealed class SelectFrameTool : McpTool<SelectFrameArgs>
{
    public override string Name => "debug_select_frame";
    public override string Description =>
        "Choose which call-stack frame debug_get_locals, debug_evaluate and debug_expand read, by " +
        "the index debug_get_callstack reports (0 = where execution is paused). Locals belong to a " +
        "frame: stopped inside a method that was called, the caller's variables are out of scope " +
        "until you select its frame — that is what \"not in scope in the current frame\" means. " +
        "Same as double-clicking a line in the Call Stack window. The selection lasts until the " +
        "program runs again. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(SelectFrameArgs args)
    {
        var r = await IdeDebugService.Instance.SelectFrameAsync(args.Index);
        return !r.Ok
            ? new { ok = false, inBreak = r.InBreak, reason = r.Reason }
            : (object)new
            {
                ok = true,
                index = r.Index,
                function = r.Function,
                frameCount = r.FrameCount,
            };
    }
}

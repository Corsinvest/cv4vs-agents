/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SelectThreadArgs
{
    [Required, Description("Thread id from debug_list_threads.")]
    public int ThreadId { get; set; }
}

/// <summary>MCP tool: make another thread the one the call stack and inspection tools read.</summary>
internal sealed class SelectThreadTool : McpTool<SelectThreadArgs>
{
    public override string Name => "debug_select_thread";
    public override string Description =>
        "Choose which thread debug_get_callstack, debug_get_locals and the rest read, by the id " +
        "debug_list_threads reports. They all look at one thread, so on multi-threaded code the " +
        "others are invisible until you switch. The frame selection starts over at the top of the " +
        "new thread's stack — frames belong to a thread — so debug_select_frame after this, not " +
        "before. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(SelectThreadArgs args)
    {
        var r = await IdeDebugService.Instance.SelectThreadAsync(args.ThreadId);
        return !r.Ok
            ? new { ok = false, inBreak = r.InBreak, reason = r.Reason }
            : (object)new { ok = true, threadId = r.ThreadId, name = r.Name };
    }
}

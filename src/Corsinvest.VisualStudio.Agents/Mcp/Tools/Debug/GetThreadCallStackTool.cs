/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetThreadCallStackArgs
{
    [Required, Description("Thread id, as debug_list_threads reports it.")]
    public int ThreadId { get; set; }
}

/// <summary>MCP tool: another thread's call stack without selecting that thread — the read that
/// debug_select_thread + debug_get_callstack would do at the cost of moving the debugger.</summary>
internal sealed class GetThreadCallStackTool : McpTool<GetThreadCallStackArgs>
{
    public override string Name => "debug_get_thread_callstack";
    public override string Description =>
        "Get one thread's call stack by id WITHOUT making it the current thread. Use it to survey " +
        "several threads — chasing a deadlock, seeing what a worker is blocked on — where " +
        "debug_select_thread + debug_get_callstack would move the debugger's current thread each " +
        "time, taking the user's Call Stack and Locals windows with it and never putting them " +
        "back. Frames carry no file or line here: the IDE reads those from the selected frame, so " +
        "they would describe a different thread. Needs break mode; debug_list_threads has the ids.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(GetThreadCallStackArgs args)
    {
        var r = await IdeDebugService.Instance.GetThreadCallStackAsync(args.ThreadId);
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            threadId = args.ThreadId,
            frames = r.Frames.Select(f => new { index = f.Index, function = f.Function, module = f.Module }).ToArray(),
        };
    }
}

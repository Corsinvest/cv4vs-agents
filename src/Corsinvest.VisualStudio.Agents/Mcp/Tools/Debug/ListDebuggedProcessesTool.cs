/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: the processes the current session is debugging. Separate from
/// debug_list_processes, which lists what could be attached to rather than what is live.</summary>
internal sealed class ListDebuggedProcessesTool : McpTool<NoArgs>
{
    public override string Name => "debug_list_debugged_processes";
    public override string Description =>
        "List the processes THIS debug session is attached to — not the machine's processes, which " +
        "is debug_list_processes. Each with its pid, name, thread count and transport. The one the " +
        "other debug tools read comes FIRST and carries isCurrent: the call stack, the locals, the " +
        "threads and the console all act on that single process without naming it, so when a " +
        "solution launches several (a web app and its worker, a client and its service) this is " +
        "what shows which one you are looking at, and that the others exist. Only useful once " +
        "debugging has started.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ListDebuggedProcessesAsync();
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            processes = r.Processes.Select(p => new
            {
                pid = p.Pid,
                name = p.Name,
                threadCount = p.ThreadCount,
                isCurrent = p.IsCurrent,
                transport = p.Transport,
                userName = p.UserName,
            }).ToArray(),
        };
    }
}

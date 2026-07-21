/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ListProcessesArgs
{
    [Description("Optional name substring to filter processes (case-insensitive).")]
    public string NameFilter { get; set; }
}

/// <summary>MCP tool: list local processes the debugger can attach to. Use before debug_attach to
/// find the pid of the app you want to debug.</summary>
internal sealed class ListProcessesTool : McpTool<ListProcessesArgs>
{
    public override string Name => "debug_list_processes";
    public override string Description =>
        "List local processes the debugger can attach to (pid + name). Optionally filter by a " +
        "name substring. Use this to find the process to pass to debug_attach.";

    protected override async Task<object> InvokeAsync(ListProcessesArgs args)
    {
        var r = await IdeDebugService.Instance.ListProcessesAsync(args.NameFilter);
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            processes = r.Processes.Select(p => new { pid = p.Pid, name = p.Name }).ToArray(),
        };
    }
}

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
        "List local processes the debugger can attach to: pid, name (the file alone), path (the " +
        "full one, which is what tells two same-named processes apart) and whether something is " +
        "already debugging them. Optionally filter by a substring, matched against the full path — " +
        "so a folder narrows the list as well as a name. Use this to find the process to pass to " +
        "debug_attach: beingDebugged=true is why an attach would be refused, and is worth checking " +
        "first, since the refusal talks about the attach rather than about the state. For the " +
        "processes THIS session is debugging, use debug_list_debugged_processes — name has the " +
        "same shape in both, so the two listings can be matched up.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ListProcessesArgs args)
    {
        var r = await IdeDebugService.Instance.ListProcessesAsync(args.NameFilter);
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            processes = r.Processes.Select(p => new
            {
                pid = p.Pid,
                name = p.Name,
                path = p.Path,
                beingDebugged = p.BeingDebugged,
            }).ToArray(),
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class AttachDebugArgs
{
    [Description("PID of the process to attach to (preferred — unambiguous). Use debug_list_processes to find it.")]
    public int Pid { get; set; }

    [Description("Process name substring to attach to, if you don't have the PID. Must match exactly one process.")]
    public string ProcessName { get; set; }
}

/// <summary>MCP tool: attach the debugger to an already-running local process. The primary AI
/// debugging workflow — the app is running, attach and inspect rather than launching with F5.</summary>
internal sealed class AttachDebugTool : McpTool<AttachDebugArgs>
{
    public override string Name => "debug_attach";
    public override string Description =>
        "Attach the debugger to an already-running local process, by pid (preferred) or by a " +
        "unique name substring. Use this instead of debug_start when the app is already running " +
        "(web server, service, console). After attaching, the session is running — use debug_break " +
        "or set a breakpoint to pause it, then inspect. Find the pid with debug_list_processes.";

    protected override async Task<object> InvokeAsync(AttachDebugArgs args)
    {
        var r = await IdeDebugService.Instance.AttachAsync(args.Pid, args.ProcessName);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

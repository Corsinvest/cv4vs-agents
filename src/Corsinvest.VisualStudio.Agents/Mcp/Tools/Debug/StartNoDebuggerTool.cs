/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class StartNoDebuggerArgs
{
    [Description("Optional: project to run. If set, it becomes the startup project first. Omit to run the current startup project.")]
    public string ProjectName { get; set; }
}

/// <summary>MCP tool: Start Without Debugging (Ctrl+F5) — run the program with no debugger
/// attached. Complements debug_start (which attaches the debugger).</summary>
internal sealed class StartNoDebuggerTool : McpTool<StartNoDebuggerArgs>
{
    public override string Name => "debug_start_no_debugger";
    public override string Description =>
        "Start the program WITHOUT the debugger (equivalent to Ctrl+F5). Optionally pass a " +
        "project name to set it as startup first. Use debug_start instead when you need " +
        "breakpoints. Returns ok or ok=false with a reason.";

    protected override async Task<object> InvokeAsync(StartNoDebuggerArgs args)
    {
        var r = await IdeDebugService.Instance.StartWithoutDebuggingAsync(args.ProjectName);
        return r.Ok ? (object)new { ok = true } : new { ok = false, reason = r.Reason };
    }
}

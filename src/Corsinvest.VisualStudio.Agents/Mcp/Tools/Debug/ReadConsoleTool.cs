/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ReadConsoleArgs
{
    [Description("Lines to keep from the end (default 200, 0 for the whole buffer).")]
    public int TailLines { get; set; } = 200;

    [Description("PID of the debugged process. Omit for the first one the debugger reports.")]
    public int ProcessId { get; set; }
}

/// <summary>MCP tool: read the real console of a debugged console app — its stdout, which the
/// Debug output pane never sees.</summary>
internal sealed class ReadConsoleTool : McpTool<ReadConsoleArgs>
{
    public override string Name => "debug_console_read";
    public override string Description =>
        "Read what a debugged console application has written to its console window. This is the " +
        "program's real stdout, which is NOT in the Debug output pane — that pane only carries " +
        "Debug.WriteLine, so a Console.WriteLine prompt is invisible there. Use it to see what the " +
        "program printed, and to find out whether it is sitting at a prompt waiting for input; " +
        "debug_console_send answers it. Needs a running debug session and a project that has a " +
        "console — a GUI or web app has none.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ReadConsoleArgs args)
    {
        var r = await IdeConsoleService.Instance.ReadAsync(args.ProcessId, args.TailLines);
        return r.Ok
            ? new { ok = true, processId = r.ProcessId, content = r.Content, totalLines = r.TotalLines, truncated = r.Truncated }
            : (object)new { ok = false, reason = r.Reason };
    }
}

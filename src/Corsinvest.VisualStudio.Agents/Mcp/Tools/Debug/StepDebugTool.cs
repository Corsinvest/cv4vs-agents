/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class StepDebugArgs
{
    [Description("Direction: 'over' (default, run the line without entering calls), 'into' (enter calls), or 'out' (run to the end of the current method).")]
    public string Direction { get; set; }
}

/// <summary>MCP tool: step the paused program by one statement (over/into/out). Returns the new
/// location. Only valid in break mode.</summary>
internal sealed class StepDebugTool : McpTool<StepDebugArgs>
{
    public override string Name => "debug_step";
    public override string Description =>
        "Step the paused program by one statement. Direction: 'over' (run the line without " +
        "entering called methods — default), 'into' (step into the call), 'out' (run to the end " +
        "of the current method). Returns the new file/line. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(StepDebugArgs args)
    {
        var r = await IdeDebugService.Instance.StepAsync(args.Direction);
        return new { ok = r.Ok, mode = r.Mode, file = r.File, line = r.Line, reason = r.Reason };
    }
}

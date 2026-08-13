/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetBreakpointArgs
{
    [Required, Description("Path to the file where the breakpoint goes.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number for the breakpoint.")]
    public int Line { get; set; }

    [Description("Optional condition: the breakpoint only triggers when this expression is true.")]
    public string Condition { get; set; }

    [Description("Only break once the line has been reached this many times. Omit (or 0) to break " +
        "every time.")]
    public int HitCount { get; set; }

    [AllowedValues("equal", "greaterOrEqual", "multiple")]
    [Description("How hitCount is read: 'equal' (default) breaks on exactly that pass, " +
        "'greaterOrEqual' on that pass and every one after, 'multiple' on every Nth pass. " +
        "Ignored without a hitCount.")]
    public string HitCountType { get; set; }
}

/// <summary>MCP tool: add a breakpoint at a file/line, optionally conditional. Works in any mode
/// (it binds when the code is loaded).</summary>
internal sealed class SetBreakpointTool : McpTool<SetBreakpointArgs>
{
    public override string Name => "debug_set_breakpoint";
    public override string Description =>
        "Add a breakpoint at a file and 1-based line. Optionally pass a condition (an expression " +
        "that must be true for the breakpoint to trigger), or a hitCount to skip the first passes " +
        "— the way to stop on the 500th iteration of a loop without a counter variable to test. " +
        "Works whether or not a debug session is running. Combine with debug_start + " +
        "debug_get_state to pause execution at this point.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(SetBreakpointArgs args)
    {
        var r = await IdeDebugService.Instance.SetBreakpointAsync(
            args.FilePath, args.Line, args.Condition, args.HitCount, args.HitCountType);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

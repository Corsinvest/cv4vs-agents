/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class EnableBreakpointArgs
{
    [Description("Path to the file the breakpoint is on. With line, identifies a file breakpoint. Omit when using functionName.")]
    public string FilePath { get; set; }

    [Description("1-based line of the breakpoint. Goes with filePath.")]
    public int Line { get; set; }

    [Description("Name of the function breakpoint to change, e.g. \"MyClass.Calculate\". Use instead of filePath+line.")]
    public string FunctionName { get; set; }

    [Required, Description("true to enable, false to disable.")]
    public bool Enabled { get; set; }
}

/// <summary>MCP tool: turn a breakpoint off without losing it. debug_remove_breakpoint is the
/// destructive twin — it takes the condition and hit-count rule with it.</summary>
internal sealed class EnableBreakpointTool : McpTool<EnableBreakpointArgs>
{
    public override string Name => "debug_enable_breakpoint";
    public override string Description =>
        "Enable or disable the breakpoint(s) at a file and 1-based line, or the ones on a function " +
        "name. Disabling is how a breakpoint in hot code stops interrupting the session while you " +
        "are trying to reach a different one — unlike debug_remove_breakpoint, which also throws " +
        "away the condition and hit-count rule it was set with and cannot put them back. " +
        "debug_list_breakpoints reports enabled for each. Works whether or not a session is running.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(EnableBreakpointArgs args)
    {
        var r = await IdeDebugService.Instance.EnableBreakpointAsync(
            args.FilePath, args.Line, args.FunctionName, args.Enabled);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

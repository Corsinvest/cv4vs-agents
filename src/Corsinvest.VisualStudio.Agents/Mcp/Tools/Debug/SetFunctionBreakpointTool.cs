/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetFunctionBreakpointArgs
{
    [Required, Description("Function name to break on, e.g. \"MyNamespace.MyClass.Calculate\" or just \"Calculate\".")]
    public string FunctionName { get; set; }

    [Description("Optional condition: the breakpoint only triggers when this expression is true.")]
    public string Condition { get; set; }
}

/// <summary>MCP tool: add a breakpoint on entry to a function by name (not file/line). Useful
/// when you know the method but not the line, and works without opening the file.</summary>
internal sealed class SetFunctionBreakpointTool : McpTool<SetFunctionBreakpointArgs>
{
    public override string Name => "debug_set_function_breakpoint";
    public override string Description =>
        "Add a breakpoint that triggers when a function is entered, identified by name " +
        "(e.g. \"MyClass.Calculate\") instead of a file and line. Optionally pass a condition. " +
        "Works whether or not a debug session is running. Use when you know the method but not " +
        "the exact line, or to avoid opening the file.";

    protected override async Task<object> InvokeAsync(SetFunctionBreakpointArgs args)
    {
        var r = await IdeDebugService.Instance.SetFunctionBreakpointAsync(args.FunctionName, args.Condition);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

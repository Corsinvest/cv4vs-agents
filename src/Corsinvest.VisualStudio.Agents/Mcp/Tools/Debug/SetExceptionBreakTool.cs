/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetExceptionBreakArgs
{
    [Required, Description("Fully-qualified exception type, e.g. 'System.NullReferenceException'.")]
    public string ExceptionName { get; set; }

    [Description("True to break when this exception is thrown (default), false to stop breaking.")]
    public bool BreakWhenThrown { get; set; } = true;

    [Description("Optional exception group; defaults to 'Common Language Runtime Exceptions'.")]
    public string Group { get; set; }
}

/// <summary>MCP tool: make the debugger break when a specific exception type is thrown (first-
/// chance), so you can catch where it originates. Works in any mode.</summary>
internal sealed class SetExceptionBreakTool : McpTool<SetExceptionBreakArgs>
{
    public override string Name => "debug_set_exception_breakpoint";
    public override string Description =>
        "Configure the debugger to break when a specific exception type is thrown (first-chance), " +
        "even if it's caught — useful to find where an exception originates. Pass the " +
        "fully-qualified type (e.g. 'System.NullReferenceException'). breakWhenThrown=false turns " +
        "it off. Works in any mode; needs a solution loaded. After it breaks, debug_get_state " +
        "reports the exception type/message.";

    protected override async Task<object> InvokeAsync(SetExceptionBreakArgs args)
    {
        var r = await IdeDebugService.Instance.SetExceptionBreakAsync(args.ExceptionName, args.BreakWhenThrown, args.Group);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

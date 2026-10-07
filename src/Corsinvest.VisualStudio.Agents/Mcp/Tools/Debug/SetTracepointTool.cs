/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetTracepointArgs
{
    [Required, Description("Path to the file where the tracepoint goes.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number for the tracepoint.")]
    public int Line { get; set; }

    [Required, Description("Text printed to the Debug output pane on every pass. Expressions in " +
        "braces are evaluated, e.g. \"total = {total}, i = {i}\"; $FUNCTION, $CALLER, $CALLSTACK " +
        "and $TID are filled in too.")]
    public string LogMessage { get; set; }

    [Description("Optional condition: the message is only printed when this expression is true.")]
    public string Condition { get; set; }

    [Description("Only print once the line has been reached this many times. Omit (or 0) to print " +
        "every time.")]
    public int HitCount { get; set; }

    [AllowedValues("equal", "greaterOrEqual", "multiple")]
    [Description("How hitCount is read: 'equal' (default) prints on exactly that pass, " +
        "'greaterOrEqual' on that pass and every one after, 'multiple' on every Nth pass. " +
        "Ignored without a hitCount.")]
    public string HitCountType { get; set; }
}

/// <summary>MCP tool: add a tracepoint, a breakpoint that prints a message and does not stop.
/// Its own tool rather than a parameter of debug_set_breakpoint because the session never pauses
/// on it: nothing in debug_get_* will have anything to report for that line.</summary>
internal sealed class SetTracepointTool : McpTool<SetTracepointArgs>
{
    public override string Name => "debug_set_tracepoint";
    public override string Description =>
        "Add a tracepoint at a file and 1-based line: each time the line is reached it prints " +
        "logMessage to the Debug output pane and the program carries on. It does NOT stop, so " +
        "debug_get_state never reports a break for it and there is nothing to inspect there: use " +
        "debug_set_breakpoint when you need to look at the state. This is the way to follow a " +
        "value across many passes of a loop in one run, or to watch code that behaves differently " +
        "when paused (a race, a timeout). ide_read_output on the Debug pane has the lines. " +
        "Optionally pass a condition or a hitCount to print on some passes only. " +
        "To Visual Studio a tracepoint is a kind of breakpoint, so the breakpoint tools handle it: " +
        "debug_list_breakpoints shows it with breaks=false, debug_remove_breakpoint removes it, " +
        "debug_enable_breakpoint switches it off and on. " +
        "Each pass costs a few milliseconds (measured: about 150 a second), so a hot loop runs " +
        "visibly slower while one is set on it. " +
        "A blank line is refused and a comment line is moved to the next statement, as for a " +
        "breakpoint: the returned line is where it sits.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(SetTracepointArgs args)
    {
        var r = await IdeDebugService.Instance.SetTracepointAsync(
            args.FilePath, args.Line, args.LogMessage, args.Condition, args.HitCount, args.HitCountType);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason, file = r.File, line = r.Line };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: read the current debugger state. Tells whether the program is stopped at a
/// breakpoint (mode='break'), which is the only state where live inspection works.</summary>
internal sealed class GetDebugStateTool : McpTool<NoArgs>
{
    public override string Name => "debug_get_state";
    public override string Description =>
        "Get the current debug state: mode is 'design' (not debugging), 'run' (running), or " +
        "'break' (paused on a breakpoint/exception). In 'break' mode also returns the current " +
        "file and 1-based line where execution is paused, and — if paused ON AN EXCEPTION — its " +
        "type and message. Poll this after debug_start to know when the program has hit a " +
        "breakpoint or thrown.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var s = await IdeDebugService.Instance.GetStateAsync();
        return new
        {
            mode = s.Mode,
            currentFile = s.CurrentFile,
            currentLine = s.CurrentLine,
            exceptionType = s.ExceptionType,
            exceptionMessage = s.ExceptionMessage,
        };
    }
}

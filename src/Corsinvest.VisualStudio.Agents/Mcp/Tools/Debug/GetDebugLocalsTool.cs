/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: list local variables in the current stack frame while paused.</summary>
internal sealed class GetDebugLocalsTool : McpTool<NoArgs>
{
    public override string Name => "debug_get_locals";
    public override string Description =>
        "List the local variables in the current stack frame while paused (break mode): each " +
        "with name, type, and value. Objects/collections aren't expanded — hasMembers=true means " +
        "you can drill in with debug_evaluate(\"name.member\"). Only valid in break mode.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.GetLocalsAsync();
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            functionName = r.FunctionName,
            locals = r.Locals.Select(l => new
            {
                name = l.Name,
                type = l.Type,
                value = l.Value,
                hasMembers = l.HasMembers,
            }).ToArray(),
        };
    }
}

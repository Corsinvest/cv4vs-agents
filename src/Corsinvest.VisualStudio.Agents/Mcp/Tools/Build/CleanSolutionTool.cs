/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: clean the solution, for when a build result looks stale
/// and the next step is to rebuild from scratch.</summary>
internal sealed class CleanSolutionTool : McpTool<NoArgs>
{
    public override string Name => "build_clean";
    public override string Description =>
        "Clean the entire solution: delete the build outputs (bin/obj) of every project. " +
        "Blocks until the clean ends. Use it when a build result looks stale, then call " +
        "build_solution to rebuild — cleaning on its own produces no diagnostics.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeContextService.Instance.CleanAsync();
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            message = r.Message,
        };
    }
}

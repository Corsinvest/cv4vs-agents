/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: build the whole solution and report success + errors, so
/// Claude can run a fix → build → fix loop using the IDE's own compiler.</summary>
internal sealed class BuildSolutionTool : McpTool<NoArgs>
{
    public override string Name => "build_solution";
    public override string Description =>
        "Build the entire solution and return whether it succeeded plus the list " +
        "of compiler errors (file, line, description). Blocks until the build ends.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeContextService.Instance.BuildAsync(null);
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            message = r.Message,
            errors = r.Errors
        };
    }
}

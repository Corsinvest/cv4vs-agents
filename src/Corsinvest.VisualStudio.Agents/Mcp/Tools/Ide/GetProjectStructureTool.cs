/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: the solution tree (projects + their source files), so Claude
/// understands the layout without globbing the disk.</summary>
internal sealed class GetProjectStructureTool : McpTool<NoArgs>
{
    public override string Name => "ide_get_project_structure";
    public override string Description =>
        "Get the solution structure: each project with its name, path, and the " +
        "files it contains. Recurses solution folders. Useful to learn the layout.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var s = await IdeContextService.Instance.GetProjectStructureAsync();
        return new
        {
            solutionPath = s.SolutionPath,
            projects = s.Projects
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class BuildProjectArgs
{
    [Required, Description("Project name (or file name without extension) to build.")]
    public string ProjectName { get; set; }
}

/// <summary>MCP tool: build a single project in the active configuration and
/// report success + errors. Faster than a full solution build for tight loops.</summary>
internal sealed class BuildProjectTool : McpTool<BuildProjectArgs>
{
    public override string Name => "build_project";
    public override string Description =>
        "Build a single project (by name) in the active configuration and return " +
        "whether it succeeded plus the list of compiler errors. Blocks until done.";

    protected override async Task<object> InvokeAsync(BuildProjectArgs args)
    {
        var r = await IdeContextService.Instance.BuildAsync(args.ProjectName);
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            message = r.Message,
            errors = r.Errors
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RemoveProjectArgs
{
    [Required, JsonProperty("project_name")]
    [Description("Name of the project to remove from the solution.")]
    public string ProjectName { get; set; }
}

/// <summary>MCP tool: take a project out of the solution without deleting it from disk — the
/// reverse of solution_add_project.</summary>
internal sealed class RemoveProjectTool : McpTool<RemoveProjectArgs>
{
    public override string Name => "solution_remove_project";
    public override string Description =>
        "Remove a project from the solution and save it. The project's files stay on disk — this " +
        "takes it out of the solution, it does not delete it. Returns ok=false with the available " +
        "project names when the name doesn't match. The reverse of solution_add_project.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(RemoveProjectArgs args)
    {
        var r = await IdeContextService.Instance.RemoveProjectFromSolutionAsync(args.ProjectName);
        return new { ok = r.Ok, project = r.Project, reason = r.Reason };
    }
}

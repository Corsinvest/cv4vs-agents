/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class AddProjectArgs
{
    [Required, JsonProperty("project_path")]
    [Description("Absolute path of an existing project file (.csproj, .vbproj, .vcxproj, …).")]
    public string ProjectPath { get; set; }
}

/// <summary>MCP tool: add an existing project to the open solution — Solution Explorer's
/// "Add → Existing Project".</summary>
internal sealed class AddProjectTool : McpTool<AddProjectArgs>
{
    public override string Name => "solution_add_project";
    public override string Description =>
        "Add an existing project file to the open solution, as Solution Explorer's " +
        "'Add → Existing Project' does, and save the solution. The project file must already " +
        "exist — this does not scaffold one. Returns the resolved project name. " +
        "solution_remove_project is the reverse; project_add_file adds a file to a project " +
        "that is already in the solution.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(AddProjectArgs args)
    {
        var r = await IdeContextService.Instance.AddProjectToSolutionAsync(args.ProjectPath);
        return new { ok = r.Ok, project = r.Project, reason = r.Reason };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetStartupProjectArgs
{
    [Required, JsonProperty("project_name")]
    [Description("Name of the project to make the startup project (the one debug_start launches).")]
    public string ProjectName { get; set; }
}

/// <summary>MCP tool: set the solution's startup project, so debug_start / build target the
/// right one in a multi-project solution.</summary>
internal sealed class SetStartupProjectTool : McpTool<SetStartupProjectArgs>
{
    public override string Name => "build_set_startup_project";
    public override string Description =>
        "Set the solution's startup project — the one debug_start (F5) launches. Pass the " +
        "project name; returns ok plus the resolved startup project, or ok=false with " +
        "the list of available projects if the name doesn't match.";

    protected override async Task<object> InvokeAsync(SetStartupProjectArgs args)
    {
        var r = await IdeContextService.Instance.SetStartupProjectAsync(args.ProjectName);
        return new { ok = r.Ok, startup_project = r.StartupProject, reason = r.Reason };
    }
}

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

    [AllowedValues("error", "warning", "all")]
    [Description("How far down the Error List to report: 'error' (default), 'warning' for errors " +
                 "and warnings, or 'all' to include informational items. Each level means that one " +
                 "and everything more severe.")]
    public string Severity { get; set; }
}

/// <summary>MCP tool: build a single project in the active configuration and
/// report success + errors. Faster than a full solution build for tight loops.</summary>
internal sealed class BuildProjectTool : McpTool<BuildProjectArgs>
{
    public override string Name => "build_project";
    public override string Description =>
        "Build a single project (by name) in the active configuration and return whether it " +
        "succeeded plus what the Error List holds (file, line, description, severity). Blocks " +
        "until done. Reports errors only unless severity says otherwise; the message says how " +
        "many items were left out, and 'configuration' says which one it built — " +
        "solution_set_configuration changes it. Trust ok/failedProjects/message rather than the " +
        "length of 'errors': the Error List only holds diagnostics for files open in an editor, so " +
        "a failed build can come back with an empty list; ide_read_output('Build') has the full " +
        "compiler log. The name is a project name, not a path — " +
        "ide_get_project_structure lists them. build_solution builds everything instead.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(BuildProjectArgs args)
    {
        var r = await IdeContextService.Instance.BuildAsync(args.ProjectName, args.Severity ?? "error");
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            configuration = r.Configuration,
            message = r.Message,
            errors = r.Errors
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RemoveFileFromProjectArgs
{
    [Required, JsonProperty("project_name")]
    [Description("Project to remove the file from.")]
    public string ProjectName { get; set; }

    [Required, JsonProperty("file_path")]
    [Description("Absolute path of the file as the project lists it.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: take a file out of a project without deleting it — the reverse of
/// project_add_file.</summary>
internal sealed class RemoveFileFromProjectTool : McpTool<RemoveFileFromProjectArgs>
{
    public override string Name => "project_remove_file";
    public override string Description =>
        "Remove a file from a project. The file stays on disk — this takes it out of the build, " +
        "it does not delete it. The reverse of project_add_file.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(RemoveFileFromProjectArgs args)
    {
        var r = await IdeContextService.Instance.RemoveFileFromProjectAsync(args.ProjectName, args.FilePath);
        return new { ok = r.Ok, project = r.Project, reason = r.Reason };
    }
}

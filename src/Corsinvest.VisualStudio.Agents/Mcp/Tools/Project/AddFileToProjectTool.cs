/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class AddFileToProjectArgs
{
    [Required, JsonProperty("project_name")]
    [Description("Project to add the file to. ide_get_project_structure lists the names.")]
    public string ProjectName { get; set; }

    [Required, JsonProperty("file_path")]
    [Description("Absolute path of a file that already exists on disk.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: add an existing file to a project, for project types that list their files
/// explicitly — where writing the file to disk is not enough to get it compiled.</summary>
internal sealed class AddFileToProjectTool : McpTool<AddFileToProjectArgs>
{
    public override string Name => "project_add_file";
    public override string Description =>
        "Add a file that already exists on disk to a project, as Solution Explorer's " +
        "'Add → Existing Item' does. Needed for project types that list every file explicitly: " +
        "there, a .cs written to disk compiles in the IDE but is missing from an MSBuild command-" +
        "line build, which fails silently. SDK-style projects glob their files in and need no " +
        "call — one made anyway reports that the file is already included. This does not create " +
        "the file: write it first, then add it. project_remove_file is the reverse.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(AddFileToProjectArgs args)
    {
        var r = await IdeContextService.Instance.AddFileToProjectAsync(args.ProjectName, args.FilePath);
        return new { ok = r.Ok, project = r.Project, reason = r.Reason };
    }
}

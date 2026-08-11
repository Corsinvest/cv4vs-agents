/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: returns the IDE's "workspace folders" (in our case,
/// the single solution folder), under the <c>getWorkspaceFolders</c> name the
/// CLI already knows.</summary>
internal sealed class GetWorkspaceFoldersTool : McpTool<NoArgs>
{
    public override string Name => "ide_get_workspace_folders";
    public override string Description =>
        "Get the workspace folders currently open in the IDE — the solution folder, for Visual " +
        "Studio. Empty when no solution is loaded, which is also why the tools needing one " +
        "(build_*, nav_*, document_format) would fail; ide_get_project_structure lists what is " +
        "inside it.";

    public override bool ReadOnly => true;

    public override bool Idempotent => true;
    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var folders = await IdeContextService.Instance.GetWorkspaceFoldersAsync();
        return new { folders };
    }
}

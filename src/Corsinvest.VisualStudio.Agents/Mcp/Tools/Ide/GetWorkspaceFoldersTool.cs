/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: returns the IDE's "workspace folders" (in our case,
/// the single solution folder). Mirrors the VS Code extension's
/// <c>getWorkspaceFolders</c>.</summary>
internal sealed class GetWorkspaceFoldersTool : McpTool<NoArgs>
{
    public override string Name => "ide_get_workspace_folders";
    public override string Description =>
        "Get the workspace folders currently open in the IDE. " +
        "Returns the solution folder for Visual Studio.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var folders = await IdeContextService.Instance.GetWorkspaceFoldersAsync();
        return new { folders };
    }
}

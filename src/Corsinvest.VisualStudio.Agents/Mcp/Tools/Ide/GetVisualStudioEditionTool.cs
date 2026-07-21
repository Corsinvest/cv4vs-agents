/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: the Visual Studio edition (Enterprise / Professional /
/// Community), so Claude knows which SKU-specific features are available.</summary>
internal sealed class GetVisualStudioEditionTool : McpTool<NoArgs>
{
    public override string Name => "ide_get_edition";
    public override string Description =>
        "Get the Visual Studio edition (e.g. \"Enterprise\", \"Professional\", " +
        "\"Community\").";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var (_, _, _, edition) = await IdeContextService.Instance.GetIdeInfoAsync();
        return new { edition };
    }
}

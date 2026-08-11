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
        "\"Community\"). The edition and the installed workloads decide what the tools can do at " +
        "all — a supported=false from a nav_* or debug_* tool is usually this rather than a bug. " +
        "ide_get_version gives the version alongside it.";

    public override bool ReadOnly => true;

    public override bool Idempotent => true;
    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var (_, _, _, edition) = await IdeContextService.Instance.GetIdeInfoAsync();
        return new { edition };
    }
}

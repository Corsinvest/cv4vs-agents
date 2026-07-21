/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: the running Visual Studio version (marketing name + year +
/// raw DTE version), so Claude can tailor commands/APIs to the IDE.</summary>
internal sealed class GetVisualStudioVersionTool : McpTool<NoArgs>
{
    public override string Name => "ide_get_version";
    public override string Description =>
        "Get the running Visual Studio version: name (e.g. \"Visual Studio 2026\"), " +
        "marketing year, and raw DTE version (e.g. \"18.0\").";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var (name, year, version, _) = await IdeContextService.Instance.GetIdeInfoAsync();
        return new { name, year, version };
    }
}

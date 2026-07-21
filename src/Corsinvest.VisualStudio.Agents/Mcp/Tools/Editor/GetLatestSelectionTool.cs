/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: returns the most recent non-empty selection,
/// regardless of which editor currently has focus.</summary>
internal sealed class GetLatestSelectionTool : McpTool<NoArgs>
{
    public override string Name => "editor_get_latest_selection";
    public override string Description =>
        "Get the most recent non-empty selection from any editor, even if " +
        "focus has moved away. Returns null if no selection has been made.";
    public override bool AlwaysLoad => true;

    protected override Task<object> InvokeAsync(NoArgs args)
    {
        var sel = IdeContextService.Instance.GetLatestSelection();
        return Task.FromResult<object>(new { selection = sel });
    }
}

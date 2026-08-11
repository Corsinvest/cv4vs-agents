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
        "Get the most recent non-empty selection from any editor, even after focus has moved away " +
        "— the one to use when the user selected something and then came to the chat. It is " +
        "remembered by editor_get_selection, so it stays null until that has been called at least " +
        "once in this session; a fresh session sees nothing here even if text is selected on " +
        "screen.";
    public override bool ReadOnly => true;

    public override bool Idempotent => true;
    public override bool AlwaysLoad => true;

    protected override Task<object> InvokeAsync(NoArgs args)
    {
        var sel = IdeContextService.Instance.GetLatestSelection();
        return Task.FromResult<object>(new { selection = sel });
    }
}

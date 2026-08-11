/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: returns the active editor's current selection
/// (text + range). Empty selection = caret only. Null when no text
/// document is active.</summary>
internal sealed class GetCurrentSelectionTool : McpTool<NoArgs>
{
    public override string Name => "editor_get_selection";
    public override string Description =>
        "Get the current text selection in the active editor: the selected text and its range, or " +
        "null when no editor has focus — which includes right after the user clicks elsewhere, " +
        "since the selection belongs to the focused window. Calling this also remembers the answer " +
        "for editor_get_latest_selection; reach for that one instead when the user may have moved " +
        "on since selecting.";
    public override bool ReadOnly => true;

    public override bool Idempotent => true;
    public override bool AlwaysLoad => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var sel = await IdeContextService.Instance.GetCurrentSelectionAsync();
        // Cache it so editor_get_latest_selection still works after focus leaves the editor.
        IdeContextService.Instance.RememberSelection(sel);
        return new { selection = sel };
    }
}

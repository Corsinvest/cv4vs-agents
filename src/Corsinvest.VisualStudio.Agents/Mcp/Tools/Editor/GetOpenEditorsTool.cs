/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: lists files open in editor tabs, each with active
/// (focused) and dirty (unsaved) flags.</summary>
internal sealed class GetOpenEditorsTool : McpTool<NoArgs>
{
    public override string Name => "editor_get_open_files";
    public override string Description =>
        "List files currently open in the IDE's editor tabs, with " +
        "active/dirty flags and language id.";
    public override bool AlwaysLoad => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var editors = await IdeContextService.Instance.GetOpenEditorsAsync();
        return new { editors };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: close every diff/compare window currently open in
/// the IDE. Returns the number of windows that were closed.</summary>
internal sealed class CloseAllDiffTabsTool : McpTool<NoArgs>
{
    public override string Name => "editor_close_all_diffs";
    public override string Description =>
        "Close every diff window this server opened, and only those: a window of the user's own " +
        "is left alone even if its caption says Diff. Returns how many were closed. Use it to tidy " +
        "up after a series of editor_open_diff calls; editor_close_tab closes one by name.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var closed = await IdeContextService.Instance.CloseAllDiffTabsAsync();
        return new
        {
            closedCount = closed
        };
    }
}

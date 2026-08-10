/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class CloseTabArgs
{
    [Required, JsonProperty("tab_name")]
    public string TabName { get; set; }
}

/// <summary>MCP tool: close a single tab in the IDE by name (cleanup
/// path used by useDiffInIDE after a diff preview).
/// Tool name is snake_case (<c>close_tab</c>) — fixed by the CLI.</summary>
internal sealed class CloseTabTool : McpTool<CloseTabArgs>
{
    public override string Name => "editor_close_tab";
    public override string Description =>
        "Close a diff tab this server opened, by the tabName that editor_open_diff was given. It " +
        "does NOT close arbitrary editor tabs: only frames in our own diff registry are touched, " +
        "so the user's documents are safe from it — and closing something they opened is not on " +
        "offer. Finding no such tab is not an error. Use editor_close_all_diffs to clear them all.";

    protected override async Task<object> InvokeAsync(CloseTabArgs args)
    {
        await IdeContextService.Instance.CloseTabAsync(args.TabName);
        return new { success = true };
    }
}

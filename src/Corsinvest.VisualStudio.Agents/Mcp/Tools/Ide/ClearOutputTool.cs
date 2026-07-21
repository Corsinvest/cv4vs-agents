/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ClearOutputArgs
{
    [Required, Description("Output pane name to clear, e.g. 'Build', 'Debug'.")]
    public string Pane { get; set; }
}

/// <summary>MCP tool: clear a VS Output window pane. Used before an action so a later
/// ide_read_output returns only fresh output (clear → action → read).</summary>
internal sealed class ClearOutputTool : McpTool<ClearOutputArgs>
{
    public override string Name => "ide_clear_output";
    public override string Description =>
        "Clear a Visual Studio Output window pane (by name). Run it before an action so a " +
        "later ide_read_output returns only the fresh output, not the old history. The pane " +
        "name is required (no clear-all). Returns ok; ok=false with availablePanes when the " +
        "pane isn't found.";

    protected override async Task<object> InvokeAsync(ClearOutputArgs args)
    {
        var r = await IdeOutputService.Instance.ClearAsync(args.Pane);
        if (!r.Ok) { return new { ok = false, reason = r.Reason, availablePanes = r.AvailablePanes }; }
        return new { ok = true, pane = r.Pane };
    }
}

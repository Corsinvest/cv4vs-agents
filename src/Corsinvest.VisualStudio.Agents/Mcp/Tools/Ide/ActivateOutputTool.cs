/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ActivateOutputArgs
{
    [Required, Description("Output pane name to bring to the foreground, e.g. 'Build', 'Debug'.")]
    public string Pane { get; set; }
}

/// <summary>MCP tool: bring a VS Output window pane to the foreground so the user sees it —
/// e.g. at a debug checkpoint, to show the build/debug output before asking for confirmation.</summary>
internal sealed class ActivateOutputTool : McpTool<ActivateOutputArgs>
{
    public override string Name => "ide_activate_output";
    public override string Description =>
        "Bring a Visual Studio Output window pane (by name) to the foreground so the user sees " +
        "it. Use at a debug checkpoint to show the relevant build/debug output before asking " +
        "the user to confirm. The pane name is required. Returns ok; ok=false with " +
        "availablePanes when the pane isn't found.";

    protected override async Task<object> InvokeAsync(ActivateOutputArgs args)
    {
        var r = await IdeOutputService.Instance.ActivateAsync(args.Pane);
        if (!r.Ok) { return new { ok = false, reason = r.Reason, availablePanes = r.AvailablePanes }; }
        return new { ok = true, pane = r.Pane };
    }
}

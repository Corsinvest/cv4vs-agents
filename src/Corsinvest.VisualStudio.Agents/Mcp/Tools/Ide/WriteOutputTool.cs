/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class WriteOutputArgs
{
    [Required, Description("Output pane to write to. A pane that doesn't exist is created, so " +
        "this can be a name of your own — writing into 'Build' or 'Debug' mixes your text with " +
        "the IDE's.")]
    public string Pane { get; set; }

    [Required, Description("Text to write. A trailing newline is added when missing.")]
    public string Text { get; set; }

    [Description("Bring the pane to the front after writing (default false).")]
    public bool Activate { get; set; }
}

/// <summary>MCP tool: write a line into an Output pane, creating it when needed — a place to
/// leave progress or a note where the user will see it, without a dialog.</summary>
internal sealed class WriteOutputTool : McpTool<WriteOutputArgs>
{
    public override string Name => "ide_write_output";
    public override string Description =>
        "Write text to a Visual Studio Output window pane, creating the pane if it doesn't exist. " +
        "Use it to leave progress or a note where the user can see it — it appears in the IDE " +
        "rather than only in the conversation, and it survives the turn. Prefer a pane name of " +
        "your own over 'Build' or 'Debug', which VS writes to. Set 'activate' to bring it to the " +
        "front; without it the write is silent. ide_read_output reads panes back.";

    protected override async Task<object> InvokeAsync(WriteOutputArgs args)
    {
        var r = await IdeOutputService.Instance.WriteAsync(args.Pane, args.Text, args.Activate);
        return r.Ok
            ? new { ok = true, pane = r.Pane }
            : (object)new { ok = false, reason = r.Reason, availablePanes = r.AvailablePanes };
    }
}

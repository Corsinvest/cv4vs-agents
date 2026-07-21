/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class OpenFileArgs
{
    [Required, Description("Path to the file to open.")]
    public string FilePath { get; set; }

    [Description("Optional. 1-based first line to select. Omit to open without selecting.")]
    public int StartLine { get; set; }

    [Description("Optional. 1-based last line to select. Defaults to startLine (single line) when omitted.")]
    public int EndLine { get; set; }

    [Description("Optional. Activate (focus) the editor tab after opening.")]
    public bool Activate { get; set; }
}

/// <summary>MCP tool: open a file in the IDE editor and optionally select a
/// line range. Line-based (Claude reasons in line numbers, like Read's output).</summary>
internal sealed class OpenFileTool : McpTool<OpenFileArgs>
{
    public override string Name => "editor_open_file";
    public override string Description =>
        "Open a file in the editor. Optionally select whole lines with startLine/endLine " +
        "(1-based). Set activate to focus the tab.";

    protected override async Task<object> InvokeAsync(OpenFileArgs args)
    {
        var ok = await IdeContextService.Instance.OpenFileAsync(
            args.FilePath,
            args.StartLine,
            args.EndLine,
            args.Activate);
        return new { success = ok };
    }
}

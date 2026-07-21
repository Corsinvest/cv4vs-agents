/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>Args use snake_case wire names because that's what the
/// Claude CLI sends (see useDiffInIDE.ts on the client side). C# stays
/// PascalCase, JsonProperty bridges the gap on both serialize and
/// deserialize.</summary>
internal sealed class OpenDiffArgs
{
    [Required, JsonProperty("old_file_path")]
    public string OldFilePath { get; set; }

    [Required, JsonProperty("new_file_path")]
    public string NewFilePath { get; set; }

    [Required, JsonProperty("new_file_contents")]
    public string NewFileContents { get; set; }

    [Required, JsonProperty("tab_name")]
    public string TabName { get; set; }
}

/// <summary><para>
/// MCP tool: open a side-by-side diff between an existing file
/// and a proposed new content. Used by the CLI to preview pending edits
/// (see <c>useDiffInIDE.ts</c> on the client side).
/// </para>
/// <para>
/// Response is the special MCP "interactive content" envelope: a single
/// text block with one of <c>FILE_SAVED</c>, <c>TAB_CLOSED</c>,
/// <c>DIFF_REJECTED</c>. The CLI's <c>useDiffInIDE</c> parses the raw
/// RPC result, not <c>content[0].text</c>.
/// </para></summary>
internal sealed class OpenDiffTool : McpTool<OpenDiffArgs>
{
    public override string Name => "editor_open_diff";
    public override string Description =>
        "Open a side-by-side diff between an existing file and proposed new content.";

    protected override async Task<object> InvokeAsync(OpenDiffArgs args)
    {
        var result = await IdeContextService.Instance.OpenDiffAsync(
            args.OldFilePath, args.NewFilePath, args.NewFileContents, args.TabName);

        // Wire format mirrors the VS Code extension (2.1.169): accept →
        // [FILE_SAVED, content]; anything else → [DIFF_REJECTED, tab_name].
        // (2.1.169 dropped TAB_CLOSED; our viewer's TAB_CLOSED maps to reject.)
        if (result.Status == IdeDiffViewer.FileSaved)
        {
            return new RawMcpContent(
            [
                new { type = "text", text = IdeDiffViewer.FileSaved },
                new { type = "text", text = result.SavedContent ?? args.NewFileContents },
            ]);
        }
        return new RawMcpContent(
        [
            new { type = "text", text = IdeDiffViewer.DiffRejected },
            new { type = "text", text = args.TabName ?? "" },
        ]);
    }
}

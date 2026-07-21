/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class CheckDocumentDirtyArgs
{
    [Required, Description("Path to the file to check.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: report whether an open file has unsaved changes, so Claude
/// can decide to save (or warn) before reading/editing. Mirrors the official VS
/// Code extension's checkDocumentDirty.</summary>
internal sealed class CheckDocumentDirtyTool : McpTool<CheckDocumentDirtyArgs>
{
    public override string Name => "document_check_dirty";
    public override string Description =>
        "Check whether an open file has unsaved changes. Returns isOpen=false " +
        "when the file isn't open in any editor; otherwise isDirty true/false.";

    protected override async Task<object> InvokeAsync(CheckDocumentDirtyArgs args)
    {
        var dirty = await IdeContextService.Instance.IsDocumentDirtyAsync(args.FilePath);
        return dirty == null
            ? new
            {
                isOpen = false,
                isDirty = false
            }
            : new
            {
                isOpen = true,
                isDirty = dirty.Value
            };
    }
}

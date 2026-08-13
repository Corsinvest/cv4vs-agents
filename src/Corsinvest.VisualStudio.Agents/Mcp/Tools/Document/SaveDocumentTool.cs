/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SaveDocumentArgs
{
    [Required, Description("Path to the file to save.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: save an open document if it has unsaved changes, so Claude
/// reads/edits the on-disk version the user actually sees. Registered under the
/// <c>saveDocument</c> name the CLI already knows.</summary>
internal sealed class SaveDocumentTool : McpTool<SaveDocumentArgs>
{
    public override string Name => "document_save";
    public override string Description =>
        "Save an open file if it has unsaved changes. Returns saved=true if a " +
        "save happened, false if the file wasn't open or was already saved — the two are not " +
        "told apart here, document_check_dirty separates them beforehand. Needed after " +
        "document_format, document_organize_imports or document_run_cleanup, which change buffers " +
        "and leave them unsaved. nav_rename_symbol does not need it — it writes to disk itself.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(SaveDocumentArgs args)
    {
        var saved = await IdeContextService.Instance.SaveDocumentAsync(args.FilePath);
        return new { saved };
    }
}

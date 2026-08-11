/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class OrganizeImportsArgs
{
    [Required, Description("Path to the file to organize imports for.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: organize and remove unused using/import directives
/// via VS's <c>Edit.RemoveAndSort</c> command. Works on C# (using),
/// VB (Imports), and any language whose formatter implements the
/// command. Side effect: the file is opened in the editor.</summary>
internal sealed class OrganizeImportsTool : McpTool<OrganizeImportsArgs>
{
    public override string Name => "document_organize_imports";
    public override string Description =>
        "Organize and remove unused using/import directives in a file " +
        "via the IDE's Edit.RemoveAndSort command. " +
        "The file must live inside the open solution's folder; success=false otherwise. " +
        "Leaves the buffer unsaved, like document_format — document_save writes it out.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(OrganizeImportsArgs args)
    {
        var ok = await IdeContextService.Instance.OrganizeImportsAsync(args.FilePath);
        return new { success = ok };
    }
}

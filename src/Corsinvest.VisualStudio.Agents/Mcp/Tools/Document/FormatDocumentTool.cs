/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class FormatDocumentArgs
{
    [Required, Description("Path to the file to format.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: format a file using VS's built-in
/// <c>Edit.FormatDocument</c> command. Same engine the user invokes
/// with Ctrl+K, Ctrl+D — respects .editorconfig, analyzer rules, and
/// the language-specific formatter. Works on any language VS knows.
/// Side effect: the file is opened in the editor (single tab).</summary>
internal sealed class FormatDocumentTool : McpTool<FormatDocumentArgs>
{
    public override string Name => "document_format";
    public override string Description =>
        "Format a file using the IDE's built-in formatter. " +
        "Equivalent to Ctrl+K, Ctrl+D in Visual Studio.";

    protected override async Task<object> InvokeAsync(FormatDocumentArgs args)
    {
        var ok = await IdeContextService.Instance.FormatDocumentAsync(args.FilePath);
        return new { success = ok };
    }
}

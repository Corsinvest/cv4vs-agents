/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetDocumentSymbolsArgs
{
    [Required, Description("Path to the file whose symbols (classes, methods, …) you want.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: outline of a file's symbols (classes → methods/properties, nested),
/// each with its name, kind (Class/Method/Property/…) and 1-based line. The same tree as the
/// editor's navigation dropdown. Cheaper than reading a large file just to find where things
/// are. Multi-language where the VS language service supports it; returns supported=false
/// otherwise.</summary>
internal sealed class GetDocumentSymbolsTool : McpTool<GetDocumentSymbolsArgs>
{
    public override string Name => "nav_get_document_symbols";
    public override string Description =>
        "List a file's symbols as a tree — each with its name, kind (Class/Method/Property/…) " +
        "and 1-based line, ordered top-to-bottom — the editor's navigation outline. Useful to " +
        "locate members in a large file without reading it all. The file must belong to a " +
        "project in the open solution. Returns supported=false for languages this isn't " +
        "available for, or transiently while the solution is still loading — retry shortly.";

    protected override async Task<object> InvokeAsync(GetDocumentSymbolsArgs args)
    {
        var r = await IdeNavigationService.Instance.GetDocumentSymbolsAsync(
            args.FilePath, CancellationToken.None);

        if (!r.Supported)
        {
            return new { supported = false, reason = r.Reason };
        }
        return new { supported = true, reason = r.Reason, symbols = r.Symbols.Select(Map).ToArray() };
    }

    private static object Map(IdeNavigationService.DocSymbol s) => new
    {
        name = s.Name,
        kind = s.Kind,
        line = s.Line,
        children = s.Children.Select(Map).ToArray(),
    };
}

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

internal sealed class SearchWorkspaceSymbolsArgs
{
    [Required, Description("Name (or partial name) of the symbol to find across the whole solution, e.g. 'SessionManager' or 'ReadAsync'.")]
    public string Query { get; set; }
}

/// <summary>MCP tool: find a symbol by name across the WHOLE solution (VS "Navigate To"),
/// unlike nav_get_document_symbols (one file). Multi-language where the VS language service
/// registers NavigateTo; returns supported=false otherwise so the model falls back to grep.</summary>
internal sealed class SearchWorkspaceSymbolsTool : McpTool<SearchWorkspaceSymbolsArgs>
{
    public override string Name => "nav_search_workspace_symbols";
    public override string Description =>
        "Find a symbol by name across the entire solution (the 'Navigate To' search). " +
        "Returns up to 50 hits, each with name, kind, file and 1-based line, ordered by file " +
        "then line. Use it to locate a class/method without knowing its file. Returns " +
        "supported=false for languages without NavigateTo support — fall back to Grep then.";

    protected override async Task<object> InvokeAsync(SearchWorkspaceSymbolsArgs args)
    {
        var r = await IdeNavigationService.Instance.SearchWorkspaceAsync(args.Query, CancellationToken.None);
        if (!r.Supported) { return new { supported = false, reason = r.Reason }; }
        return new
        {
            supported = true,
            results = r.Hits.Select(h => new
            {
                name = h.Name,
                kind = h.Kind,
                file = h.File,
                line = h.Line,
                container_name = h.ContainerName,
            }).ToArray(),
        };
    }
}

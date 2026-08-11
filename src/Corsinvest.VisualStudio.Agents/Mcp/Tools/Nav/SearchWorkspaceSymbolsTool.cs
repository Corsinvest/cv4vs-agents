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
        "Find a symbol by name across the entire solution (the 'Navigate To' search). Matches " +
        "declarations, not text, so a hit is a real class/method/field and comes with its kind, " +
        "its container and the declaration line itself. Returns up to 50 hits, each with name, " +
        "kind, file, 1-based line, container_name and preview, ordered by file then line. " +
        "Returns supported=false where no project provides NavigateTo — fall back to Grep then, " +
        "which searches text and will also match usages and comments. This is the way in when " +
        "the file is unknown: from a hit, nav_go_to_definition, nav_find_references and " +
        "nav_get_document_symbols all take the file and line it returns.";

    public override bool ReadOnly => true;

    public override bool Idempotent => true;
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
                preview = h.Preview,
            }).ToArray(),
        };
    }
}

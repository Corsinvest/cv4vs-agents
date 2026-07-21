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

internal sealed class FindReferencesArgs
{
    [Required, Description("Path to the file that contains the symbol.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number where the symbol appears (the line you read).")]
    public int Line { get; set; }

    [Required, Description("The symbol name on that line whose references you want.")]
    public string SymbolName { get; set; }
}

/// <summary>MCP tool: find all REFERENCES to a symbol (semantic, not text search) — every
/// place it's used across the solution, excluding the symbol's own definition (that's
/// nav_go_to_definition). Works without opening the editor. Multi-language where the VS language
/// service supports it (C#/VB and others); returns supported=false otherwise so the model
/// can fall back to grep.</summary>
internal sealed class FindReferencesTool : McpTool<FindReferencesArgs>
{
    public override string Name => "nav_find_references";
    public override string Description =>
        "Find all references to a symbol across the solution (semantic, not text search): " +
        "give the file, the 1-based line where the symbol appears, and the symbol name. " +
        "Returns each reference's file/line (usages only — the symbol's own definition is " +
        "excluded; use nav_go_to_definition for that). The file must belong to a project in the " +
        "open solution. Returns supported=false for languages this isn't available for, or " +
        "transiently while the solution is still loading — retry shortly before using grep.";

    protected override async Task<object> InvokeAsync(FindReferencesArgs args)
    {
        var r = await IdeNavigationService.Instance.GetReferencesAsync(
            args.FilePath, args.Line, args.SymbolName, CancellationToken.None);

        if (!r.Supported)
        {
            return new { supported = false, found = false, reason = r.Reason };
        }
        return new
        {
            supported = true,
            found = r.Found,
            reason = r.Reason,
            count = r.Locations.Length,
            locations = r.Locations.Select(l => new
            {
                filePath = l.FilePath,
                line = l.Line,
                column = l.Column,
                preview = l.Preview,
            }).ToArray(),
        };
    }
}

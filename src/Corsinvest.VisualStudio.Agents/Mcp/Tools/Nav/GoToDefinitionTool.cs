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

internal sealed class GoToDefinitionArgs
{
    [Required, Description("Path to the file that contains the symbol usage.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number where the symbol appears (the line you read).")]
    public int Line { get; set; }

    [Required, Description("The symbol name on that line whose definition you want (e.g. a method or type name).")]
    public string SymbolName { get; set; }
}

/// <summary>MCP tool: resolve where a symbol is DEFINED (file + line), semantically — not a
/// text search. Works without opening the editor. Multi-language where the VS language
/// service supports it (C#/VB and others); returns "not supported" otherwise so the model
/// can fall back to grep.</summary>
internal sealed class GoToDefinitionTool : McpTool<GoToDefinitionArgs>
{
    public override string Name => "nav_go_to_definition";
    public override string Description =>
        "Find where a symbol is defined (semantic, not text search): give the file, the " +
        "1-based line where the symbol is used, and the symbol name. Returns the defining " +
        "file/line. The file must belong to a project in the open solution. Returns " +
        "supported=false for languages this isn't available for, or transiently while the " +
        "solution is still loading — safe to retry shortly before falling back to grep.";

    protected override async Task<object> InvokeAsync(GoToDefinitionArgs args)
    {
        var r = await IdeNavigationService.Instance.GetDefinitionAsync(
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

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

internal sealed class GoToImplementationArgs
{
    [Required, Description("Path to the file that contains the symbol.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number where the symbol appears (the line you read).")]
    public int Line { get; set; }

    [Required, Description("The symbol name on that line whose implementations you want.")]
    public string SymbolName { get; set; }
}

/// <summary>MCP tool: find the IMPLEMENTATIONS of a symbol — the concrete classes/members that
/// implement an interface, or override a virtual/abstract member. Different from nav_go_to_definition
/// (the declaration) and nav_find_references (the callers). Multi-language where the VS language
/// service supports it; returns supported=false otherwise.</summary>
internal sealed class GoToImplementationTool : McpTool<GoToImplementationArgs>
{
    public override string Name => "nav_go_to_implementation";
    public override string Description =>
        "Find the implementations of a symbol (semantic): for an interface or an interface member, " +
        "the concrete classes/members that implement it; for a virtual/abstract member, the " +
        "overrides. Give the file, the 1-based line where the symbol appears, and the symbol name. " +
        "Use this — not nav_find_references — to see the actual code behind an interface. The file must " +
        "belong to a project in the open solution. Returns supported=false for languages this isn't " +
        "available for, or transiently while the solution is still loading.";

    protected override async Task<object> InvokeAsync(GoToImplementationArgs args)
    {
        var r = await IdeNavigationService.Instance.GetImplementationsAsync(
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

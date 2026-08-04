/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class BuildSolutionArgs
{
    [Description("How far down the Error List to report: 'error' (default), 'warning' for errors " +
                 "and warnings, or 'all' to include informational items. Each level means that one " +
                 "and everything more severe.")]
    public string Severity { get; set; }
}

/// <summary>MCP tool: build the whole solution and report success + errors, so
/// Claude can run a fix → build → fix loop using the IDE's own compiler.</summary>
internal sealed class BuildSolutionTool : McpTool<BuildSolutionArgs>
{
    public override string Name => "build_solution";
    public override string Description =>
        "Build the entire solution and return whether it succeeded plus what the Error List holds " +
        "(file, line, description, severity). Blocks until the build ends. Reports errors only " +
        "unless severity says otherwise; the message says how many items were left out.";

    protected override async Task<object> InvokeAsync(BuildSolutionArgs args)
    {
        // An absent severity has to become the default here: passing null through would only reach
        // the parameter's default if the argument were left off entirely.
        var r = await IdeContextService.Instance.BuildAsync(null, args?.Severity ?? "error");
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            message = r.Message,
            errors = r.Errors
        };
    }
}

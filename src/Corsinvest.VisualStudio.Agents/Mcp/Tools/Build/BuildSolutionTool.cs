/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class BuildSolutionArgs
{
    [AllowedValues("error", "warning", "all")]
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
        "unless severity says otherwise; the message says how many items were left out. Prefer " +
        "this to a dotnet build in the shell: it goes through the open IDE, so there is no path to " +
        "resolve and no clash with a debug session. Builds whichever configuration the IDE has " +
        "active and reports it back as 'configuration' — solution_set_configuration changes it. " +
        "Trust ok/failedProjects/message for the outcome rather than the length of 'errors': the " +
        "Error List is filled asynchronously, so a failed build can answer before its errors have " +
        "landed, and an entry left by an earlier build or a debug session can outlive a build that " +
        "succeeded. ide_read_output('Build') has the compiler's own log when the list disagrees. " +
        "build_project builds one project instead.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(BuildSolutionArgs args)
    {
        // An absent severity has to become the default here: passing null through would only reach
        // the parameter's default if the argument were left off entirely.
        var r = await IdeContextService.Instance.BuildAsync(null, args?.Severity ?? "error");
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            configuration = r.Configuration,
            message = r.Message,
            errors = r.Errors
        };
    }
}

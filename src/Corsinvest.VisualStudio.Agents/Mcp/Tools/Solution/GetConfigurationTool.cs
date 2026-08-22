/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: report the active solution configuration and the ones available, so the
/// caller can check or choose one without building the solution to find out.</summary>
internal sealed class GetConfigurationTool : McpTool<NoArgs>
{
    public override string Name => "solution_get_configuration";
    public override string Description =>
        "Get the solution's active configuration — the one build_solution and build_project " +
        "compile — plus every configuration that can be asked for ('Debug|Any CPU', " +
        "'Release|Any CPU', …). Takes no arguments and changes nothing. Use it to check what a " +
        "build will produce, or to see the valid names before solution_set_configuration, which is " +
        "what actually switches it. " +
        "Also reports startupProject — the one debug_start runs — under the name " +
        "solution_set_startup_project takes, so it can be read before changing it and put back " +
        "after. null when none is set, or when several are.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeContextService.Instance.GetConfigurationAsync();
        return new { ok = r.Ok, configuration = r.Configuration, available = r.Available, startupProject = r.StartupProject, reason = r.Reason };
    }
}

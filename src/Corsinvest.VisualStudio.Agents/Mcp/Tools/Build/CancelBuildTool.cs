/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: stop the build that is running, so a build left going by a
/// timed-out build_solution — or one the user started by hand — does not block the next one.</summary>
internal sealed class CancelBuildTool : McpTool<NoArgs>
{
    public override string Name => "build_cancel";
    public override string Description =>
        "Stop the build currently running in the IDE and wait for it to actually stop. " +
        "Reports ok=true once the IDE is free again — including when nothing was running, since " +
        "that is the state you asked for. Use it when build_solution or build_clean reported a " +
        "timeout (the build was left running), or when a build started outside the chat is in the " +
        "way. A cancelled build leaves partial outputs, so run build_clean before trusting the " +
        "next one.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeContextService.Instance.CancelBuildAsync();
        return new
        {
            ok = r.Ok,
            failedProjects = r.FailedProjects,
            message = r.Message,
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetConfigurationArgs
{
    [Required]
    [Description("Solution configuration to activate, e.g. 'Debug' or 'Release'. The platform may " +
                 "be included as 'Release|Any CPU'; without it the first platform offered for that " +
                 "name is used.")]
    public string Configuration { get; set; }
}

/// <summary>MCP tool: switch the active solution configuration. Separate from build_solution
/// because it changes the IDE for the user and leaves it changed — a build must not have that
/// as a side effect.</summary>
internal sealed class SetConfigurationTool : McpTool<SetConfigurationArgs>
{
    public override string Name => "build_set_configuration";
    public override string Description =>
        "Switch the solution's active configuration (Debug, Release, …) — the one build_solution " +
        "and build_project compile and debug_start launches. Pass 'Debug' or 'Release', or the " +
        "full 'Release|Any CPU' when a name has several platforms; returns ok plus the resolved " +
        "configuration, or ok=false with the available ones if the name doesn't match. This is a " +
        "change to the user's IDE and it persists: the toolbar dropdown moves and their next " +
        "manual build follows it, so switch only when asked, and say so. build_get_configuration " +
        "reads the current one, and the valid names, without changing anything.";

    public override bool Destructive => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(SetConfigurationArgs args)
    {
        var r = await IdeContextService.Instance.SetConfigurationAsync(args.Configuration);
        return new { ok = r.Ok, configuration = r.Configuration, reason = r.Reason };
    }
}

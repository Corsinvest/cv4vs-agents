/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetDebugLocalsArgs
{
    [Description("Levels of members to walk, 0-3. Default 0 — names, types and values only, which " +
        "is what you want unless every object in scope is worth opening. 1 saves a debug_expand " +
        "per object, but it walks ALL of them: with a collection in scope, lower maxMembers first " +
        "or expand the one you care about instead.")]
    public int? Depth { get; set; }

    [Description("Members kept at each walked level, 1-200. Default 50 — low for one object, a lot " +
        "when every local is walked at once. Ignored at depth 0.")]
    public int? MaxMembers { get; set; }
}

/// <summary>MCP tool: the parameters and locals of the selected frame while paused.</summary>
internal sealed class GetDebugLocalsTool : McpTool<GetDebugLocalsArgs>
{
    private const int DefaultMaxMembers = 50;

    public override string Name => "debug_get_locals";
    public override string Description =>
        "List the parameters and local variables of the selected stack frame while paused (break " +
        "mode): each with name, type and value, the parameters first and marked isArgument. " +
        "Objects are flat by default — hasMembers=true means you can see inside with " +
        "debug_expand(\"name\"), or pass depth here to walk them all at once. Reads the frame " +
        "debug_select_frame chose, which is the paused one until you move it. Only valid in break " +
        "mode.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(GetDebugLocalsArgs args)
    {
        var depth = Clamp(args.Depth ?? 0, 0, 3);
        var maxMembers = Clamp(args.MaxMembers ?? DefaultMaxMembers, 1, 200);

        var r = await IdeDebugService.Instance.GetLocalsAsync(depth, maxMembers);
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            functionName = r.FunctionName,
            locals = r.Locals.Select(l => new
            {
                name = l.Name,
                type = l.Type,
                value = l.Value,
                hasMembers = l.HasMembers,
                isArgument = l.IsArgument,
                members = l.Members,
            }).ToArray(),
            truncated = r.Truncated,
        };
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}

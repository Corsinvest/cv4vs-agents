/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ExpandExpressionArgs
{
    [Required, Description("Expression to expand: a local, a field path ('entry.Profile'), 'this', " +
        "or '$exception' while paused on a throw.")]
    public string Expression { get; set; }

    [Description("Levels of members to walk, 1-3. Default 2. How much it costs depends on the " +
        "object, not the number: a flat data class barely grows past level 2, while an exception " +
        "or a framework type explodes — its static and non-public members open up too. Start at 1 " +
        "for those.")]
    public int? Depth { get; set; }

    [Description("Members kept at EACH level, 1-200. Default 50. Cutting a 60-item list to 5 also " +
        "cuts each of those 5 to its first 5 members; truncated comes back true either way.")]
    public int? MaxMembers { get; set; }
}

/// <summary>MCP tool: expand an expression into its members, recursively. The read half of what
/// debug_evaluate does one field at a time.</summary>
internal sealed class ExpandExpressionTool : McpTool<ExpandExpressionArgs>
{
    private const int DefaultDepth = 2;
    private const int DefaultMaxMembers = 50;

    public override string Name => "debug_expand";
    public override string Description =>
        "Expand an expression into its members while paused (break mode), so an object comes back " +
        "as a tree instead of just a type name: pass 'order', 'order.Customer', 'this', or " +
        "'$exception' when stopped on a throw (that one carries InnerException and the stack — " +
        "expand it at depth 1, it is a framework type and depth 3 buries the message in static " +
        "members). This is what debug_get_locals points at when it reports hasMembers=true — one call " +
        "instead of a debug_evaluate per field. hasMembers on a returned node means there is more " +
        "below it: expand that path to see it. truncated=true means a level had more members than " +
        "maxMembers and what came back is a prefix. Note: reading a property runs its getter in " +
        "the program, so this can have side-effects. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(ExpandExpressionArgs args)
    {
        // Clamped rather than rejected: a depth of 9 is a request for the whole object graph, and
        // failing the call would only cost the model a round trip to ask for something serveable.
        var depth = Clamp(args.Depth ?? DefaultDepth, 1, 3);
        var maxMembers = Clamp(args.MaxMembers ?? DefaultMaxMembers, 1, 200);

        var r = await IdeDebugService.Instance.ExpandAsync(args.Expression, depth, maxMembers);
        return !r.Ok
            ? new
            {
                ok = false,
                inBreak = r.InBreak,
                reason = r.Reason
            }
            : new
            {
                ok = true,
                expression = r.Expression,
                value = r.Value,
                type = r.Type,
                members = r.Members,
                truncated = r.Truncated,
            };
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}

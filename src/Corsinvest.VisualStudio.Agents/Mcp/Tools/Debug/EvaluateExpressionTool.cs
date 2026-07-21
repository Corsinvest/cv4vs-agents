/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class EvaluateExpressionArgs
{
    [Required, Description("Expression to evaluate in the current frame, e.g. 'myVar', 'order.Items.Count', 'customer.Name'.")]
    public string Expression { get; set; }
}

/// <summary>MCP tool: evaluate an expression in the current debug frame while paused (like the
/// Watch/Immediate window). Note it can call getters/methods, so it may have side-effects.</summary>
internal sealed class EvaluateExpressionTool : McpTool<EvaluateExpressionArgs>
{
    public override string Name => "debug_evaluate";
    public override string Description =>
        "Evaluate an expression in the current stack frame while paused (break mode), like the " +
        "Watch window: pass something like 'order.Items.Count'. Returns the value and type. " +
        "Note: evaluating can call property getters/methods in the program, so it may have " +
        "side-effects — prefer reading fields/properties. You can also assign (e.g. 'x = 5') to " +
        "change a variable's value while paused. Only valid in break mode.";

    protected override async Task<object> InvokeAsync(EvaluateExpressionArgs args)
    {
        var r = await IdeDebugService.Instance.EvaluateAsync(args.Expression);
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            expression = r.Expression,
            value = r.Value,
            type = r.Type,
            isValid = r.IsValid,
            reason = r.Reason,
        };
    }
}

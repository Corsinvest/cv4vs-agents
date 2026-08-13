/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: apply pending code edits to the running program without restarting it
/// (Hot Reload). Changes the code (not just values, unlike debug_evaluate).</summary>
internal sealed class ApplyHotReloadTool : McpTool<NoArgs>
{
    public override string Name => "debug_apply_hot_reload";
    public override string Description =>
        "Apply your pending code edits to the running program WITHOUT restarting it (Hot Reload / " +
        "Edit-and-Continue). Use after editing a file during a debug session to see the change take " +
        "effect live. Needs an active debug session. Answers ok=false when there is nothing pending " +
        "or when the pending edit needs a rebuild instead — changing a method signature or adding a " +
        "type is not something Hot Reload can take, and debug_restart is the way through. " +
        "ide_read_output has the warnings when it did run. Differs from debug_evaluate, which " +
        "changes values, not code.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ApplyHotReloadAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

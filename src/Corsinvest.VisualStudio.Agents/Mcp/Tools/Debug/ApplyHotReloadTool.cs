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
        "effect live. Needs an active debug session. " +
        "ok=true means the command ran, NOT that code changed: the IDE exposes no way to ask " +
        "whether anything was pending, so calling this with nothing to apply succeeds too. What it " +
        "actually did is in the output — read the 'Hot Reload' pane with ide_read_output. An edit " +
        "Hot Reload cannot take (a changed method signature, a new type) needs debug_restart. " +
        "Differs from debug_evaluate, which changes values, not code.";

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.ApplyHotReloadAsync();
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class FreezeThreadArgs
{
    [Required, Description("Thread id from debug_list_threads.")]
    public int ThreadId { get; set; }

    [Description("true freezes, false thaws. Default true.")]
    public bool? Freeze { get; set; }
}

/// <summary>MCP tool: hold one thread still while the rest of the program runs.</summary>
internal sealed class FreezeThreadTool : McpTool<FreezeThreadArgs>
{
    public override string Name => "debug_freeze_thread";
    public override string Description =>
        "Freeze or thaw one thread, by the id debug_list_threads reports. A frozen thread does not " +
        "run when the program resumes, which is how a race is pinned down: freeze the ones that " +
        "interfere and step the one being watched. It STAYS frozen until something thaws it — a " +
        "forgotten one makes the program behave in ways nothing else explains, so thaw it when the " +
        "investigation is over, and debug_list_threads shows isFrozen if you lose track. Freezing " +
        "everything leaves nothing to run. Only valid in break mode.";

    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(FreezeThreadArgs args)
    {
        var r = await IdeDebugService.Instance.FreezeThreadAsync(args.ThreadId, args.Freeze ?? true);
        return !r.Ok
            ? new { ok = false, inBreak = r.InBreak, reason = r.Reason }
            : (object)new { ok = true, threadId = r.ThreadId, name = r.Name, isFrozen = r.IsFrozen };
    }
}

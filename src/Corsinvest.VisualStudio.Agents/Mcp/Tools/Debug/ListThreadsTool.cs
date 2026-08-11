/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: the threads of the debugged program, with the ids the other thread tools
/// take.</summary>
internal sealed class ListThreadsTool : McpTool<NoArgs>
{
    public override string Name => "debug_list_threads";
    public override string Description =>
        "List the threads of the program being debugged while paused (break mode): each with its " +
        "id, name, location and whether it is frozen. The one the inspection tools read comes " +
        "FIRST and carries isCurrent — everything else in this domain looks at a single thread, " +
        "and this is what shows the others exist. Most threads carry no name of their own, the " +
        "main one included (it is set in code and rarely is), and come back as '(unnamed)': the " +
        "location is what identifies them. Pass an id to debug_select_thread to look at one, or to " +
        "debug_freeze_thread to hold it still. Only valid in break mode.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(NoArgs args)
    {
        var r = await IdeDebugService.Instance.GetThreadsAsync();
        if (!r.Ok) { return new { ok = false, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            threads = r.Threads.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                location = t.Location,
                isAlive = t.IsAlive,
                isFrozen = t.IsFrozen,
                isCurrent = t.IsCurrent,
            }).ToArray(),
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SetNextStatementArgs
{
    [Required, Description("Absolute path of the file holding the line to jump to. Must be the " +
        "method execution is currently paused in — the jump cannot leave the frame.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line to make the next statement. Must be executable code.")]
    public int Line { get; set; }
}

/// <summary>MCP tool: move the instruction pointer without running what lies between
/// ("Set Next Statement").</summary>
internal sealed class SetNextStatementTool : McpTool<SetNextStatementArgs>
{
    public override string Name => "debug_set_next_statement";
    public override string Description =>
        "Move the instruction pointer to this line WITHOUT running the code in between — the Set " +
        "Next Statement command. Skips a call that would fail, or jumps back to retry a block " +
        "after fixing a value with debug_evaluate. The line must be in the file execution is " +
        "paused in: the jump cannot leave the method, and asking for another file is refused here " +
        "because Visual Studio would not — it reads the number as a line of the CURRENT method and " +
        "moves there instead. SIDE-EFFECTFUL, unlike the rest of the debug tools: the skipped " +
        "statements never run, so anything they would have assigned keeps its old value, and " +
        "jumping backwards runs side effects a second time. Nothing checks that the jump makes " +
        "sense — prefer asking first. Stays in break mode, and only valid in break mode.";

    protected override async Task<object> InvokeAsync(SetNextStatementArgs args)
    {
        var r = await IdeDebugService.Instance.SetNextStatementAsync(args.FilePath, args.Line);
        return new { ok = r.Ok, mode = r.Mode, reason = r.Reason };
    }
}

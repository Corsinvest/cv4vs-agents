/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetExceptionSettingsArgs
{
    [Description("Limit to one exception group, e.g. 'Common Language Runtime Exceptions'. Omit " +
        "for every group.")]
    public string Group { get; set; }
}

/// <summary>MCP tool: which exceptions the debugger is set to break on — the read side of
/// debug_set_exception_breakpoint.</summary>
internal sealed class GetExceptionSettingsTool : McpTool<GetExceptionSettingsArgs>
{
    public override string Name => "debug_get_exception_settings";
    public override string Description =>
        "List the exception types the debugger will break on when they are THROWN, whether or not " +
        "they are handled. Only those are returned: the full list runs to thousands of types that " +
        "are all set to break on unhandled only, which is the default and says nothing. Use it " +
        "before debug_set_exception_breakpoint to see what is already configured, and to explain " +
        "why a debug session is stopping somewhere unexpected — a first-chance break the user " +
        "turned on earlier looks like a crash until you know it is set.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(GetExceptionSettingsArgs args)
    {
        var r = await IdeDebugService.Instance.GetExceptionSettingsAsync(args.Group);
        return r.Ok
            ? new
            {
                ok = true,
                count = r.Settings.Count,
                settings = r.Settings.Select(s => new { group = s.Group, name = s.Name }).ToArray(),
            }
            : (object)new { ok = false, reason = r.Reason };
    }
}

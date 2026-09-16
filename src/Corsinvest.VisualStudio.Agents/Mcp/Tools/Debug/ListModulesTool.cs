/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ListModulesArgs
{
    [Description("Return only the user's own modules ('My Code'), skipping the framework and the runtime.")]
    public bool UserCodeOnly { get; set; }
}

/// <summary>MCP tool: the modules loaded into the debugged process, with their symbol state. The
/// tool to reach for when a breakpoint refuses to bind.</summary>
internal sealed class ListModulesTool : McpTool<ListModulesArgs>
{
    public override string Name => "debug_list_modules";
    public override string Description =>
        "List the modules (DLLs/EXEs) loaded into the process being debugged: name, path, version, " +
        "whether symbols were loaded and whether the debugger counts it as user code. THIS IS THE " +
        "TOOL FOR A BREAKPOINT THAT WILL NOT BIND — a breakpoint the debugger shows as unresolved " +
        "is nearly always a module with symbolsLoaded=false, or a module that was never loaded at " +
        "all. Also answers which build of a dependency is actually in the process, when that is in " +
        "doubt. User-code modules come FIRST; pass userCodeOnly to drop the framework and runtime " +
        "rows entirely. Works while running, not only in break mode — but the list only grows as " +
        "the program loads more.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ListModulesArgs args)
    {
        var r = await IdeDebugService.Instance.ListModulesAsync(args.UserCodeOnly);
        if (!r.Ok) { return new { ok = false, supported = r.Supported, inBreak = r.InBreak, reason = r.Reason }; }
        return new
        {
            ok = true,
            inBreak = r.InBreak,
            modules = r.Modules.Select(m => new
            {
                name = m.Name,
                path = m.Path,
                version = m.Version,
                symbolsLoaded = m.SymbolsLoaded,
                symbolFile = m.SymbolFile,
                userCode = m.UserCode,
                optimized = m.Optimized,
                is64Bit = m.Is64Bit,
            }).ToArray(),
        };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RunTestsArgs
{
    [Description("Only run tests whose fully-qualified name contains one of these, e.g. " +
                 "\"FactorialTests\" or a single test's full name. Omit to run every test.")]
    public string[] Filter { get; set; }
}

/// <summary>MCP tool: run tests through the IDE's Test Explorer and wait for the run to end.</summary>
internal sealed class RunTestsTool : McpTool<RunTestsArgs>
{
    public override string Name => "test_run";
    public override string Description =>
        "Run tests through the IDE's Test Explorer, on the build and the active configuration the " +
        "IDE already has — no separate restore, no second opinion about which configuration is " +
        "current. Blocks until the run ends, then says whether it ran; test_get_results has the " +
        "per-test outcome, including the failures with their message and stack trace. Covers every " +
        "framework the IDE supports, not only .NET. Use test_run_with_debugger instead to stop on " +
        "a failure and inspect it with the debug_* tools.";

    // Runs the user's test code: neither read-only nor safely repeatable on its own.
    public override bool ReadOnly => false;

    protected override async Task<object> InvokeAsync(RunTestsArgs args)
    {
        var outcome = await IdeTestService.RunTestsAsync(args?.Filter, debug: false);
        if (!outcome.Supported) { return new { supported = false, reason = outcome.Message }; }
        return new
        {
            supported = true,
            started = outcome.Started,
            // Told plainly: "did not start" reads like "nothing failed" if the caller only looks at
            // the results, and the two are opposite situations.
            message = outcome.Started
                ? "Run finished — call test_get_results for the outcome."
                : "The Test Explorer did not start a run: nothing matched the filter, the build "
                  + "failed, or a run was already going.",
        };
    }
}

/// <summary>MCP tool: the same run, under the IDE's debugger.</summary>
internal sealed class DebugTestsTool : McpTool<RunTestsArgs>
{
    public override string Name => "test_run_with_debugger";
    public override string Description =>
        "Run tests under the IDE's debugger, so execution stops where one fails and the debug_* " +
        "tools can read the state there — debug_get_locals, debug_get_callstack, debug_evaluate. " +
        "This is the part no test runner outside the IDE can offer. Set the breakpoints you want " +
        "first (debug_set_breakpoint) and filter down to the failing test, otherwise the whole " +
        "suite runs under a debugger for nothing. test_run is the plain, faster version.";

    // A separate tool rather than a flag on test_run: after this one the debugger owns the session
    // and the debug_* tools have something to report, which a flag set turns ago would hide.
    public override bool ReadOnly => false;

    protected override async Task<object> InvokeAsync(RunTestsArgs args)
    {
        var outcome = await IdeTestService.RunTestsAsync(args?.Filter, debug: true);
        if (!outcome.Supported) { return new { supported = false, reason = outcome.Message }; }
        return new
        {
            supported = true,
            started = outcome.Started,
            message = outcome.Started
                ? "Debug run finished. If it stopped at a breakpoint, debug_get_state says so."
                : "The Test Explorer did not start a run: nothing matched the filter, the build "
                  + "failed, or a run was already going.",
        };
    }
}

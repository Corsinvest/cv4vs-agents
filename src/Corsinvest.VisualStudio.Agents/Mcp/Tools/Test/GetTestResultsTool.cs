/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetTestResultsArgs
{
    [Description("Only tests whose fully-qualified name contains one of these. Omit for all.")]
    public string[] Filter { get; set; }

    [Description("Only the failures. Use it on a large suite where the passing tests are noise.")]
    public bool FailedOnly { get; set; }
}

/// <summary>MCP tool: the last run's per-test outcome, failures first.</summary>
internal sealed class GetTestResultsTool : McpTool<GetTestResultsArgs>
{
    public override string Name => "test_get_results";
    public override string Description =>
        "The last test run's outcome, per test: name, project, duration, and for a failure its " +
        "assertion message and stack trace — the stack carries the file and line, so a failure can " +
        "be opened straight away instead of being hunted for. Failures are listed first. Reports " +
        "what the Test Explorer holds now, so run test_run first, or read the results of a run " +
        "started from the IDE.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(GetTestResultsArgs args)
    {
        var results = await IdeTestService.GetResultsAsync(args?.Filter, args?.FailedOnly ?? false);
        if (results == null)
        {
            IdeTestService.IsAvailable(out var reason);
            return new { supported = false, reason };
        }

        // Counted by the platform's own outcome, so a skipped test is skipped rather than folded
        // into "not passed" — the two mean different things to whoever reads this.
        var failed = results.Count(r => r.Failed);
        var skipped = results.Count(r => string.Equals(r.Outcome, "Skipped", System.StringComparison.OrdinalIgnoreCase));

        if (results.Count > 0)
        {
            return new
            {
                supported = true,
                total = results.Count,
                passed = results.Count - failed - skipped,
                failed,
                skipped,
                results,
            };
        }

        // Nothing at all is the one count that does not speak for itself: with failedOnly it is the
        // good news, without it means no run has happened or the filter matched nothing. Say which,
        // because the three read identically as a bare zero.
        var note = (args?.FailedOnly ?? false) ? "No failures among the tests that match."
            : args?.Filter?.Any(f => !string.IsNullOrWhiteSpace(f)) == true
                ? "No results for that filter — either no test matches it, or the matching ones "
                + "have not been run yet. test_list shows what is discovered."
                : "No results yet: run test_run first, or run the tests from the IDE.";

        return new { supported = true, total = 0, passed = 0, failed = 0, skipped = 0, results, note };
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ListTestsArgs
{
    [Description("Only tests whose fully-qualified name contains one of these, e.g. \"FactorialTests\" " +
                 "or \"MyProject.Integration\". Omit for every test the Test Explorer knows.")]
    public string[] Filter { get; set; }
}

/// <summary>MCP tool: the tests the Test Explorer has already discovered.</summary>
internal sealed class ListTestsTool : McpTool<ListTestsArgs>
{
    public override string Name => "test_list";
    public override string Description =>
        "List the tests the IDE's Test Explorer has discovered, with their project, class and the " +
        "assembly they came from. This is the Test Explorer's own tree — the same tests it would " +
        "run, found by the same adapters, so it covers every framework the IDE supports (xUnit, " +
        "NUnit, MSTest, GoogleTest, Boost, CppUnitTest…) and not just .NET. Nothing is rebuilt and " +
        "nothing is re-discovered. An empty list on a solution that has tests usually means the " +
        "Test Explorer has not discovered them yet — build the solution, or open the window once.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ListTestsArgs args)
    {
        var tests = await IdeTestService.ListTestsAsync(args?.Filter);
        if (tests == null)
        {
            IdeTestService.IsAvailable(out var reason);
            return new { supported = false, reason };
        }
        if (tests.Count > 0) { return new { supported = true, count = tests.Count, tests }; }

        // An empty list is not an answer on its own: unfiltered, it reads as "this solution has no
        // tests" when it usually means discovery has not finished — it runs in the background after
        // a build, so an early answer is empty. Under a filter, though, empty is a real answer, and
        // blaming discovery there sends the caller chasing a problem that is not theirs.
        var filtered = args?.Filter?.Any(f => !string.IsNullOrWhiteSpace(f)) == true;
        return new { supported = true, count = 0, tests, note = filtered
            ? "No discovered test matches that filter. It matches against the fully-qualified name, "
            + "so try a shorter fragment, or call test_list with no filter to see what is there."
            : "The Test Explorer has discovered nothing yet. Discovery runs in the background after "
            + "a build, so try again in a moment; failing that, build the solution, or run test_run, "
            + "which discovers first." };
    }
}

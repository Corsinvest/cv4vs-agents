/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel;
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
        // An empty list is not an answer on its own: it reads as "this solution has no tests" when
        // it usually means discovery has not run. Say which, rather than let it be guessed — and
        // say the part that a build alone does not fix: the Test Explorer only fills its store once
        // its window has been opened in this session, so until then it answers nothing.
        return tests.Count == 0
            ? new { supported = true, count = 0, tests, note = "The Test Explorer has discovered "
                  + "nothing yet. Open Test > Test Explorer in the IDE once — it does not populate "
                  + "until its window has been opened — then build the solution, or run test_run, "
                  + "which discovers first." }
            : (object)new { supported = true, count = tests.Count, tests };
    }
}

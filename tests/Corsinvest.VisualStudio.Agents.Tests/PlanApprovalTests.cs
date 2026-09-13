/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What goes back to the CLI when a plan edited in the editor is approved.
/// <para>Both directions fail quietly. Send nothing for a real edit and the CLI re-reads a file that
/// may not hold it; send a plan for an untouched one and the model is told the user rewrote it —
/// and the CLI writes that text over the file.</para></summary>
public class PlanApprovalTests
{
    private const string Snapshot = "# Plan\n- a\n";

    [Fact]
    public void UpdatedInputFor_is_empty_when_nothing_changed()
        => Assert.Empty(PlanApproval.UpdatedInputFor(Snapshot, Snapshot));

    [Theory]
    // What a VS save can add to a file nobody edited.
    [InlineData("\uFEFF# Plan\n- a\n")]
    [InlineData("# Plan\r\n- a\r\n")]
    [InlineData("\uFEFF# Plan\r\n- a\n")]
    public void UpdatedInputFor_ignores_what_a_save_adds(string saved)
        => Assert.Empty(PlanApproval.UpdatedInputFor(Snapshot, saved));

    [Fact]
    public void UpdatedInputFor_sends_a_real_edit_with_LF_only()
    {
        var input = PlanApproval.UpdatedInputFor(Snapshot, "# Plan\r\n- a\r\n- b\r\n");

        // Only the plan: the CLI replaces the input wholesale, and planFilePath is never read back.
        Assert.Single(input.Properties());
        Assert.Equal("# Plan\n- a\n- b\n", (string)input["plan"]);
    }

    [Fact]
    public void UpdatedInputFor_keeps_non_ASCII_text_intact()
    {
        var input = PlanApproval.UpdatedInputFor("# План\n- шаг\n", "# План\n- шаг один\n");

        Assert.Equal("# План\n- шаг один\n", (string)input["plan"]);
    }

    [Fact]
    public void UpdatedInputFor_is_empty_when_nothing_could_be_read()
        => Assert.Empty(PlanApproval.UpdatedInputFor(Snapshot, null));

    [Theory]
    [InlineData("""{"planFilePath":"C:\\Users\\u\\.claude\\plans\\p.md"}""", @"C:\Users\u\.claude\plans\p.md")]
    [InlineData("""{"plan":"x"}""", null)]
    [InlineData("""{"planFilePath":""}""", null)]
    [InlineData("""{"planFilePath":42}""", null)]
    public void PlanFilePathOf_takes_only_a_non_empty_string(string json, string expected)
        => Assert.Equal(expected, PlanApproval.PlanFilePathOf(JObject.Parse(json)));

    [Fact]
    public void PlanFilePathOf_and_PlanOf_tolerate_no_input()
    {
        Assert.Null(PlanApproval.PlanFilePathOf(null));
        Assert.Equal("", PlanApproval.PlanOf(null));
    }

    [Theory]
    [InlineData(@"C:\Users\u\.claude\plans\p.md", "c:/users/U/.claude/plans/P.md", true)]
    [InlineData(@"C:\Users\u\.claude\plans\p.md", @"C:\Users\u\.claude\plans\p.md\", true)]
    [InlineData(@"C:\Users\u\.claude\plans\p.md", @"C:\Users\u\.claude\plans\p-agent-1.md", false)]
    [InlineData(null, @"C:\p.md", false)]
    public void SamePath_matches_the_way_the_shell_spells_a_moniker(string a, string b, bool expected)
        => Assert.Equal(expected, PlanApproval.SamePath(a, b));
}

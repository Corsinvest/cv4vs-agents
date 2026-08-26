/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The extras that ride along with a tool result: an edit's hunks, an Agent run's totals.
/// <para>Two sources have to agree — the live <c>toolUseResult</c> and the fields history lifts onto
/// the message — because the same row is built from one or the other depending on whether you are
/// watching it happen or reopening the session. Drift between them shows up as a diff that renders
/// live and vanishes on reload.</para></summary>
public class ToolResultExtrasTests
{
    private static JObject Hunk() => new()
    {
        ["oldStart"] = 10,
        ["oldLines"] = 2,
        ["newStart"] = 10,
        ["newLines"] = 3,
        ["lines"] = new JArray { "-old line", "+new line", "+added line" },
    };

    [Fact]
    public void FromToolUseResult_reads_the_patch_the_CLI_computed()
    {
        var result = new JObject { ["structuredPatch"] = new JArray { Hunk() } };

        var extras = ToolResultExtras.FromToolUseResult(result);

        Assert.NotNull(extras.Patch);
        var hunk = Assert.Single(extras.Patch);
        Assert.Equal(10, hunk.OldStart);
        Assert.Equal(3, hunk.NewLines);
    }

    [Fact]
    public void FromToolUseResult_treats_an_empty_patch_as_no_patch()
    {
        // How "nothing changed" arrives. Rendering it would draw an empty diff box.
        var result = new JObject { ["structuredPatch"] = new JArray() };

        Assert.Null(ToolResultExtras.FromToolUseResult(result).Patch);
    }

    [Fact]
    public void FromToolUseResult_has_no_patch_when_the_tool_reported_none()
    {
        // A Write to a new file has nothing to diff against.
        Assert.Null(ToolResultExtras.FromToolUseResult(new JObject()).Patch);
    }

    [Fact]
    public void FromToolUseResult_reads_an_agent_runs_totals()
    {
        var result = new JObject
        {
            ["totalDurationMs"] = 4200L,
            ["totalTokens"] = 15000L,
            ["totalToolUseCount"] = 7,
        };

        var totals = ToolResultExtras.FromToolUseResult(result).AgentTotals;

        Assert.NotNull(totals);
        Assert.Equal(4200, totals.DurationMs);
        Assert.Equal(15000, totals.Tokens);
        Assert.Equal(7, totals.ToolUses);
    }

    [Fact]
    public void FromToolUseResult_reports_no_totals_when_the_duration_is_zero()
    {
        // A zero duration is how both sources say "nothing to report" — a running agent, an
        // interrupted one, any other tool. A DTO full of zeros would claim the run took no time.
        var result = new JObject
        {
            ["totalDurationMs"] = 0L,
            ["totalTokens"] = 0L,
            ["totalToolUseCount"] = 0,
        };

        Assert.Null(ToolResultExtras.FromToolUseResult(result).AgentTotals);
    }

    [Fact]
    public void FromToolUseResult_survives_a_null_result()
    {
        // The object is a bare string on an error result, which reaches here as null.
        var extras = ToolResultExtras.FromToolUseResult(null);

        Assert.Null(extras.Patch);
        Assert.Null(extras.AgentTotals);
    }

    [Fact]
    public void FromMessage_reads_back_what_history_lifted_onto_the_message()
    {
        // Replay: toolUseResult does not survive into the transcript, so the pieces travel flat on
        // the message — except the patch, which stays the CLI's own JSON.
        var msg = new JObject
        {
            ["diffPatch"] = new JArray { Hunk() },
            ["agentDurationMs"] = 4200L,
            ["agentTokens"] = 15000L,
            ["agentToolUses"] = 7,
        };

        var extras = ToolResultExtras.FromMessage(msg);

        Assert.Single(extras.Patch);
        Assert.Equal(4200, extras.AgentTotals.DurationMs);
        Assert.Equal(15000, extras.AgentTotals.Tokens);
        Assert.Equal(7, extras.AgentTotals.ToolUses);
    }

    [Fact]
    public void FromMessage_and_FromToolUseResult_agree_on_the_same_run()
    {
        // The same row, built live and rebuilt from history: if these two drift the diff renders
        // while you watch and disappears when you reopen the session.
        var live = ToolResultExtras.FromToolUseResult(new JObject
        {
            ["structuredPatch"] = new JArray { Hunk() },
            ["totalDurationMs"] = 4200L,
            ["totalTokens"] = 15000L,
            ["totalToolUseCount"] = 7,
        });
        var replayed = ToolResultExtras.FromMessage(new JObject
        {
            ["diffPatch"] = new JArray { Hunk() },
            ["agentDurationMs"] = 4200L,
            ["agentTokens"] = 15000L,
            ["agentToolUses"] = 7,
        });

        Assert.Equal(live.Patch.Length, replayed.Patch.Length);
        Assert.Equal(live.Patch[0].OldStart, replayed.Patch[0].OldStart);
        Assert.Equal(live.AgentTotals.DurationMs, replayed.AgentTotals.DurationMs);
        Assert.Equal(live.AgentTotals.Tokens, replayed.AgentTotals.Tokens);
        Assert.Equal(live.AgentTotals.ToolUses, replayed.AgentTotals.ToolUses);
    }

    [Fact]
    public void FromMessage_on_a_message_carrying_neither_reports_neither()
    {
        var extras = ToolResultExtras.FromMessage(new JObject());

        Assert.Null(extras.Patch);
        Assert.Null(extras.AgentTotals);
    }

    [Fact]
    public void ToDto_is_null_when_there_is_nothing_to_report()
    {
        // So the WebView tests for presence rather than for a zero that could also mean "ran for 0ms".
        Assert.Null(new ToolResultExtras().ToDto());
    }

    [Fact]
    public void ToDto_carries_whichever_group_the_tool_reported()
    {
        var patchOnly = ToolResultExtras.FromToolUseResult(
            new JObject { ["structuredPatch"] = new JArray { Hunk() } }).ToDto();

        Assert.NotNull(patchOnly);
        Assert.NotNull(patchOnly.Patch);
        Assert.Null(patchOnly.AgentTotals);
    }
}

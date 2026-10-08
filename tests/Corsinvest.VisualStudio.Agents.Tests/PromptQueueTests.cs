/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What the CLI says about prompts written on stdin while a turn runs.
/// <para>Each answer decides whether a bubble stays on screen. Read one wrong and the chat shows
/// the model a message it never got, or hides one it did.</para></summary>
public class PromptQueueTests
{
    [Fact]
    public void Lifecycle_line_gives_the_prompt_and_its_state()
    {
        var line = JObject.Parse("""{"type":"command_lifecycle","command_uuid":"u1","state":"started","uuid":"x","session_id":"s"}""");
        Assert.True(PromptQueue.TryParseLifecycle(line, out var uuid, out var state));
        Assert.Equal("u1", uuid);
        Assert.Equal("started", state);
    }

    [Theory]
    [InlineData("""{"type":"command_lifecycle","state":"started"}""")]
    [InlineData("""{"type":"command_lifecycle","command_uuid":"","state":"started"}""")]
    [InlineData("""{"type":"command_lifecycle","command_uuid":"u1"}""")]
    [InlineData("""{"type":"command_lifecycle","command_uuid":"u1","state":""}""")]
    public void Lifecycle_line_without_a_prompt_or_a_state_is_not_one(string json)
        => Assert.False(PromptQueue.TryParseLifecycle(JObject.Parse(json), out _, out _));

    [Theory]
    [InlineData("cancelled", true)]
    [InlineData("discarded", true)]
    [InlineData("refused", true)]
    [InlineData("queued", false)]
    [InlineData("started", false)]
    [InlineData("completed", false)]
    [InlineData("somethingNew", false)]
    public void Only_three_states_mean_the_prompt_will_never_run(string state, bool gone)
        => Assert.Equal(gone, PromptQueue.IsGone(state));

    [Fact]
    public void Receipt_lists_what_survived_and_what_was_cancelled()
    {
        var r = PromptQueue.ParseInterruptReceipt(JObject.Parse("""{"still_queued":["a"],"cancelled":["b","c"]}"""));
        Assert.True(r.Known);
        Assert.Equal(new[] { "a" }, r.StillQueued);
        Assert.Equal(new[] { "b", "c" }, r.Cancelled);
    }

    [Fact]
    public void Receipt_without_a_cancelled_list_cancelled_nothing()
    {
        var r = PromptQueue.ParseInterruptReceipt(JObject.Parse("""{"still_queued":["a","b"]}"""));
        Assert.True(r.Known);
        Assert.Equal(new[] { "a", "b" }, r.StillQueued);
        Assert.Empty(r.Cancelled);
    }

    [Theory]
    // An older CLI answers an interrupt with an empty success: nothing is known, so nothing may
    // be taken off screen on the strength of it.
    [InlineData("{}")]
    [InlineData("""{"still_queued":"a"}""")]
    public void Receipt_from_a_cli_that_sends_none_is_unknown(string json)
    {
        var r = PromptQueue.ParseInterruptReceipt(JObject.Parse(json));
        Assert.False(r.Known);
        Assert.Empty(r.StillQueued);
        Assert.Empty(r.Cancelled);
    }

    [Fact]
    public void Receipt_from_a_null_response_is_unknown()
        => Assert.False(PromptQueue.ParseInterruptReceipt(null).Known);

    [Fact]
    public void Receipt_skips_entries_that_are_not_strings()
    {
        var r = PromptQueue.ParseInterruptReceipt(JObject.Parse("""{"still_queued":["a",1,null,""],"cancelled":[{"x":1},"b"]}"""));
        Assert.Equal(new[] { "a" }, r.StillQueued);
        Assert.Equal(new[] { "b" }, r.Cancelled);
    }

    [Theory]
    [InlineData("now", "now")]
    [InlineData("next", "next")]
    [InlineData("later", "later")]
    [InlineData("LATER", null)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Priority_is_one_of_the_three_the_cli_knows_or_nothing(string given, string expected)
        => Assert.Equal(expected, PromptQueue.NormalizePriority(given));
}

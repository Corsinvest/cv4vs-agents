/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat;
using Corsinvest.VisualStudio.Agents.Contracts;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Turning wire content blocks into what the WebView renders.
/// <para>The failures here are all of the same kind: the chat still renders, just wrong. A block
/// type nobody handles disappears without a trace, and usage attached to the wrong block makes the
/// context gauge count one turn several times.</para></summary>
public class ContentBlockTranslatorTests
{
    /// <summary>Collects what the translator emitted, in order.</summary>
    private sealed class Sent
    {
        public List<(string Channel, object Payload)> All { get; } = [];

        public void Send(string channel, object payload) => All.Add((channel, payload));

        public IEnumerable<T> Of<T>() => All.Select(x => x.Payload).OfType<T>();
    }

    private static JArray Blocks(params JObject[] blocks) => new(blocks.Cast<object>().ToArray());

    private static JObject Text(string text) => new()
    {
        ["type"] = "text",
        ["text"] = text,
    };

    private static JObject ToolUse(string type, string id, string name) => new()
    {
        ["type"] = type,
        ["id"] = id,
        ["name"] = name,
        ["input"] = new JObject { ["file_path"] = @"C:\proj\demo\Program.cs" },
    };

    private static JObject Usage(int input, int output, int cacheRead = 0, int cacheCreation = 0) => new()
    {
        ["input_tokens"] = input,
        ["output_tokens"] = output,
        ["cache_read_input_tokens"] = cacheRead,
        ["cache_creation_input_tokens"] = cacheCreation,
    };

    [Fact]
    public void EmitAssistant_turns_a_text_block_into_one_message()
    {
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(Blocks(Text("hello")), sent.Send, uuid: "a1");

        var text = Assert.Single(sent.Of<AssistantTextNotification>());
        Assert.Equal("hello", text.Text);
        Assert.Equal("a1", text.Uuid);
    }

    [Fact]
    public void EmitAssistant_gives_every_block_of_a_message_the_same_uuid()
    {
        // The uuid names the MESSAGE, not the block: the WebView groups by it.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(Text("one"), Text("two"), Text("three")), sent.Send, uuid: "a1");

        Assert.All(sent.Of<AssistantTextNotification>(), t => Assert.Equal("a1", t.Uuid));
    }

    [Fact]
    public void EmitAssistant_attaches_usage_to_the_first_block_only()
    {
        // The gauge adds up what it receives, so usage repeated on each block counts the turn twice.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(Text("one"), Text("two")), sent.Send, usage: Usage(100, 50));

        var texts = sent.Of<AssistantTextNotification>().ToList();
        Assert.Equal(2, texts.Count);
        Assert.NotNull(texts[0].Usage);
        Assert.Equal(100, texts[0].Usage.InputTokens);
        Assert.Equal(50, texts[0].Usage.OutputTokens);
        Assert.Null(texts[1].Usage);
    }

    [Fact]
    public void EmitAssistant_reads_every_token_field_of_the_usage()
    {
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(Text("hi")), sent.Send, usage: Usage(10, 20, cacheRead: 30, cacheCreation: 40));

        var usage = Assert.Single(sent.Of<AssistantTextNotification>()).Usage;
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(30, usage.CacheReadTokens);
        Assert.Equal(40, usage.CacheCreationTokens);
    }

    [Fact]
    public void EmitAssistant_sends_no_usage_when_the_turn_carried_none()
    {
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(Blocks(Text("hi")), sent.Send);

        Assert.Null(Assert.Single(sent.Of<AssistantTextNotification>()).Usage);
    }

    [Theory]
    // All three carry the same {id,name,input} shape, so all three render as a tool row. Handling
    // only "tool_use" would drop a web_search or an MCP call from the transcript silently.
    [InlineData("tool_use")]
    [InlineData("server_tool_use")]
    [InlineData("mcp_tool_use")]
    public void EmitAssistant_renders_every_tool_use_shape_as_a_tool_row(string type)
    {
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(ToolUse(type, "t1", "Read")), sent.Send);

        Assert.Single(sent.Of<ToolPermissionNotification>());
    }

    [Fact]
    public void EmitAssistant_moves_usage_onto_a_leading_tool_use_too()
    {
        // "First block of the turn" is about position, not about being text.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(ToolUse("tool_use", "t1", "Read"), Text("after")), sent.Send, usage: Usage(7, 3));

        Assert.NotNull(Assert.Single(sent.Of<ToolPermissionNotification>()).Usage);
        Assert.Null(Assert.Single(sent.Of<AssistantTextNotification>()).Usage);
    }

    [Fact]
    public void EmitAssistant_closes_a_thinking_block_so_the_label_stops_streaming()
    {
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(new JObject { ["type"] = "thinking", ["thinking"] = "..." }), sent.Send);

        var ended = Assert.Single(sent.Of<ThinkingEndedNotification>());
        Assert.False(ended.Redacted);
    }

    [Fact]
    public void EmitAssistant_marks_a_redacted_thinking_block_as_redacted()
    {
        // Cipher-only, no text to show: the WebView renders a static box rather than an empty one.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(new JObject { ["type"] = "redacted_thinking", ["data"] = "cipher" }), sent.Send);

        Assert.True(Assert.Single(sent.Of<ThinkingEndedNotification>()).Redacted);
    }

    [Fact]
    public void EmitAssistant_ignores_content_that_is_not_a_block_array()
    {
        // Assistant content is always an array in practice; anything else is bailed on rather than
        // guessed at.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(JValue.CreateString("bare string"), sent.Send);

        Assert.Empty(sent.All);
    }

    [Fact]
    public void EmitAssistant_carries_the_error_onto_the_message()
    {
        // What colours the red dot on the bubble.
        var sent = new Sent();

        ContentBlockTranslator.EmitAssistant(
            Blocks(Text("API Error: 529")), sent.Send, error: "overloaded");

        Assert.Equal("overloaded", Assert.Single(sent.Of<AssistantTextNotification>()).Error);
    }
}

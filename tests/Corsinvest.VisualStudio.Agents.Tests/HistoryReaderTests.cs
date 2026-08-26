/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The .jsonl readers, against a transcript on disk.
/// <para>The Compact/Pretty pairs are the point of the file: the scan matches on strings for speed,
/// so a writer that puts a space after the colon must be read identically. That rule is stated in
/// CLAUDE.md and enforced by nothing else.</para></summary>
public class HistoryReaderTests
{
    private static List<JObject> Conversation() =>
    [
        Jsonl.UserPrompt("u1", "first question"),
        Jsonl.Assistant("a1", "first answer", "u1"),
        Jsonl.UserPrompt("u2", "second question", "a1"),
        Jsonl.Assistant("a2", "second answer", "u2"),
        Jsonl.UserPrompt("u3", "third question", "a2"),
        Jsonl.Assistant("a3", "third answer", "u3"),
    ];

    [Fact]
    public void ReadHistoryRaw_returns_the_messages_oldest_first()
    {
        using var fx = SessionFixture.Compact(Conversation());

        var page = fx.Manager().ReadHistoryRaw(fx.SessionId);

        Assert.Equal(6, page.Messages.Count);
        Assert.Equal("u1", page.Messages[0]["uuid"]?.Value<string>());
        Assert.Equal("a3", page.Messages[5]["uuid"]?.Value<string>());
    }

    [Fact]
    public void ReadHistoryRaw_reads_a_pretty_printed_transcript_the_same_way()
    {
        var conversation = Conversation();
        using var compact = SessionFixture.Compact(conversation);
        using var pretty = SessionFixture.Pretty(conversation);

        var fromCompact = compact.Manager().ReadHistoryRaw(compact.SessionId);
        var fromPretty = pretty.Manager().ReadHistoryRaw(pretty.SessionId);

        Assert.Equal(fromCompact.Messages.Count, fromPretty.Messages.Count);
        Assert.Equal(
            fromCompact.Messages.Select(m => m["uuid"]?.Value<string>()),
            fromPretty.Messages.Select(m => m["uuid"]?.Value<string>()));
    }

    [Fact]
    public void ReadHistoryRaw_on_a_session_that_does_not_exist_is_empty_not_a_throw()
    {
        using var fx = SessionFixture.Compact(Conversation());

        var page = fx.Manager().ReadHistoryRaw("11111111-2222-3333-4444-555555555555");

        Assert.Empty(page.Messages);
        Assert.Equal(-1, page.OldestOffset);
    }

    [Theory]
    // A traversal token must not reach Path.Combine: it degrades to "no such session", never to
    // whatever the walked-to path happens to hold.
    [InlineData("../../etc/passwd")]
    [InlineData(@"..\..\windows\system32\config")]
    [InlineData("..")]
    public void ReadHistoryRaw_refuses_a_traversal_id(string hostileId)
    {
        using var fx = SessionFixture.Compact(Conversation());

        var page = fx.Manager().ReadHistoryRaw(hostileId);

        Assert.Empty(page.Messages);
    }

    [Fact]
    public void ReadHistoryRaw_pages_backwards_without_losing_or_repeating_a_message()
    {
        using var fx = SessionFixture.Compact(Conversation());
        var mgr = fx.Manager();

        // Two at a time, walking back from the end.
        var first = mgr.ReadHistoryRaw(fx.SessionId, 2, -1, out var info);
        Assert.Equal(2, first.Messages.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(info);

        var second = mgr.ReadHistoryRaw(fx.SessionId, 2, first.OldestOffset, out _);
        Assert.Equal(2, second.Messages.Count);

        var third = mgr.ReadHistoryRaw(fx.SessionId, 2, second.OldestOffset, out _);
        Assert.Equal(2, third.Messages.Count);
        Assert.False(third.HasMore);

        // Every uuid exactly once, and in order once the pages are stitched back together.
        var seen = third.Messages.Concat(second.Messages).Concat(first.Messages)
                        .Select(m => m["uuid"]?.Value<string>()).ToList();
        Assert.Equal(new[] { "u1", "a1", "u2", "a2", "u3", "a3" }, seen);
    }

    [Fact]
    public void ReadHistoryRaw_walks_a_transcript_larger_than_one_64KB_chunk()
    {
        // The reverse read works in 64 KB chunks and prepends older ones so a split never lands
        // mid-line. Padding each answer pushes the conversation well past one chunk, so the first
        // and last messages sit in different chunks - where an off-by-one on the boundary shows.
        var padding = new string('x', 4096);
        var lines = new List<JObject>();
        for (int i = 0; i < 40; i++)
        {
            lines.Add(Jsonl.UserPrompt($"u{i}", $"question {i}"));
            lines.Add(Jsonl.Assistant($"a{i}", $"answer {i} {padding}", $"u{i}"));
        }
        using var fx = SessionFixture.Compact(lines);

        var page = fx.Manager().ReadHistoryRaw(fx.SessionId, 80, -1, out _);

        Assert.Equal(80, page.Messages.Count);
        Assert.Equal("u0", page.Messages[0]["uuid"]?.Value<string>());
        Assert.Equal("a39", page.Messages[79]["uuid"]?.Value<string>());
        // Nothing garbled at a boundary: every line still carries the uuid it was written with.
        Assert.Equal(
            Enumerable.Range(0, 40).SelectMany(i => new[] { $"u{i}", $"a{i}" }),
            page.Messages.Select(m => m["uuid"]?.Value<string>()));
    }

    [Fact]
    public void ReadHistoryRaw_survives_a_multibyte_char_on_a_chunk_boundary()
    {
        // The window is decoded in one block precisely because a chunked backward scan split
        // multi-byte UTF-8 at the boundary and garbled accented characters in titles. The padding
        // is ASCII so it alone sets the byte offsets, and the accented text sits at both ends.
        const string firstText = "café naïve résumé";          // Latin-1 range, 2 bytes each
        const string lastText = "日本語 — emoji 😀";           // 3-byte CJK and a 4-byte pair
        var padding = new string('a', 30000);
        var lines = new List<JObject>
        {
            Jsonl.UserPrompt("u1", firstText),
            Jsonl.Assistant("a1", "answer " + padding, "u1"),
            Jsonl.UserPrompt("u2", lastText, "a1"),
        };
        using var fx = SessionFixture.Compact(lines);

        var page = fx.Manager().ReadHistoryRaw(fx.SessionId, 10, -1, out _);

        // What lands in Messages is each record's `message` object, with the line's uuid lifted
        // onto it, so the text is at ["content"] rather than ["message"]["content"].
        Assert.Equal(3, page.Messages.Count);
        Assert.Equal(firstText, page.Messages[0]["content"]?.Value<string>());
        Assert.Equal(lastText, page.Messages[2]["content"]?.Value<string>());
    }

    [Fact]
    public void ReadUserPrompts_returns_only_the_user_turns_oldest_first()
    {
        using var fx = SessionFixture.Compact(Conversation());

        var prompts = fx.Manager().ReadUserPrompts(fx.SessionId);

        // Collected newest-first while walking backwards, then reversed for the WebView.
        Assert.Equal(new[] { "first question", "second question", "third question" }, prompts);
    }

    [Fact]
    public void ReadUserPrompts_reads_a_pretty_printed_transcript_the_same_way()
    {
        var conversation = Conversation();
        using var compact = SessionFixture.Compact(conversation);
        using var pretty = SessionFixture.Pretty(conversation);

        Assert.Equal(
            compact.Manager().ReadUserPrompts(compact.SessionId),
            pretty.Manager().ReadUserPrompts(pretty.SessionId));
    }

    [Fact]
    public void ReadRewindableUuids_keeps_only_the_snapshots_that_tracked_a_file()
    {
        var lines = Conversation();
        lines.Add(Jsonl.FileHistorySnapshot("u2", @"C:\proj\demo\Program.cs"));
        lines.Add(Jsonl.EmptySnapshot("u3"));
        using var fx = SessionFixture.Compact(lines);

        var rewindable = fx.Manager().ReadRewindableUuids(fx.SessionId);

        Assert.Contains("u2", rewindable);
        // A snapshot with no tracked backups restores nothing, so it is not a rewind point.
        Assert.DoesNotContain("u3", rewindable);
        Assert.DoesNotContain("u1", rewindable);
    }

    [Fact]
    public void ReadRewindableUuids_reads_a_pretty_printed_transcript_the_same_way()
    {
        var lines = Conversation();
        lines.Add(Jsonl.FileHistorySnapshot("u2", @"C:\proj\demo\Program.cs"));
        using var compact = SessionFixture.Compact(lines);
        using var pretty = SessionFixture.Pretty(lines);

        Assert.Equal(
            compact.Manager().ReadRewindableUuids(compact.SessionId).OrderBy(x => x),
            pretty.Manager().ReadRewindableUuids(pretty.SessionId).OrderBy(x => x));
    }

    [Fact]
    public void ReadMessageBlock_returns_the_block_at_the_index()
    {
        using var fx = SessionFixture.Compact(Conversation());

        var block = fx.Manager().ReadMessageBlock(fx.SessionId, "a2", 0);

        Assert.NotNull(block);
        Assert.Equal("text", block["type"]?.Value<string>());
        Assert.Equal("second answer", block["text"]?.Value<string>());
    }

    [Fact]
    public void ReadMessageBlock_past_the_end_is_null_not_a_throw()
    {
        using var fx = SessionFixture.Compact(Conversation());
        Assert.Null(fx.Manager().ReadMessageBlock(fx.SessionId, "a2", 9));
    }

    [Fact]
    public void ReadMessageBlock_for_an_unknown_uuid_is_null()
    {
        using var fx = SessionFixture.Compact(Conversation());
        Assert.Null(fx.Manager().ReadMessageBlock(fx.SessionId, "nope", 0));
    }
}

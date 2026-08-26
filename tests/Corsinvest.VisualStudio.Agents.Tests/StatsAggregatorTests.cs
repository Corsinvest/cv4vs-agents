/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Stats;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The usage numbers behind the Statistics tab.
/// <para>Wrong arithmetic here is the quietest defect in the codebase: the dialog shows a number,
/// it looks plausible, and nothing about it says it is off. There is no manual check that catches
/// a total that is 3% low.</para></summary>
public class StatsAggregatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "cv4vs-stats-" + Guid.NewGuid().ToString("N"));

    public StatsAggregatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* temp folder; the OS reclaims it */ }
    }

    private string WriteJsonl(params JObject[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jsonl");
        var sb = new StringBuilder();
        foreach (var line in lines) { sb.Append(line.ToString(Newtonsoft.Json.Formatting.None)).Append('\n'); }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static JObject Assistant(string model, int input, int output,
                                     int cacheRead = 0, int cacheCreation = 0,
                                     string timestamp = "2026-08-26T10:00:00.000Z",
                                     string cwd = @"C:\proj\demo") => new()
    {
        ["type"] = "assistant",
        ["timestamp"] = timestamp,
        ["cwd"] = cwd,
        ["message"] = new JObject
        {
            ["role"] = "assistant",
            ["model"] = model,
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "hi" } },
            ["usage"] = new JObject
            {
                ["input_tokens"] = input,
                ["output_tokens"] = output,
                ["cache_read_input_tokens"] = cacheRead,
                ["cache_creation_input_tokens"] = cacheCreation,
            },
        },
    };

    private static JObject UserWithBlocks(string timestamp, params string[] blockTypes)
    {
        var content = new JArray();
        foreach (var t in blockTypes) { content.Add(new JObject { ["type"] = t }); }
        return new JObject
        {
            ["type"] = "user",
            ["timestamp"] = timestamp,
            ["cwd"] = @"C:\proj\demo",
            ["message"] = new JObject { ["role"] = "user", ["content"] = content },
        };
    }

    [Fact]
    public void AggregateFile_sums_tokens_per_model()
    {
        var path = WriteJsonl(
            Assistant("claude-opus-5", input: 100, output: 50, cacheRead: 10, cacheCreation: 5),
            Assistant("claude-opus-5", input: 200, output: 75),
            Assistant("claude-sonnet-5", input: 30, output: 20));

        var result = StatsAggregator.AggregateFile(path, isSubagent: false, fromOffset: 0, seed: null);

        Assert.NotNull(result);
        var agg = result.Value.agg;
        Assert.Equal(3, agg.Messages);

        var opus = agg.ModelUsage["claude-opus-5"];
        Assert.Equal(300, opus.InputTokens);
        Assert.Equal(125, opus.OutputTokens);
        Assert.Equal(10, opus.CacheReadTokens);
        Assert.Equal(5, opus.CacheCreationTokens);

        var sonnet = agg.ModelUsage["claude-sonnet-5"];
        Assert.Equal(30, sonnet.InputTokens);
        Assert.Equal(20, sonnet.OutputTokens);
    }

    [Fact]
    public void AggregateFile_keeps_the_raw_model_id()
    {
        // Deliberately provider-agnostic: a GLM or z.ai id set through env must survive as written,
        // so the id is never rewritten into a display name here.
        var path = WriteJsonl(Assistant("glm-4.6", input: 10, output: 5));

        var agg = StatsAggregator.AggregateFile(path, false, 0, null).Value.agg;

        Assert.True(agg.ModelUsage.ContainsKey("glm-4.6"));
    }

    [Fact]
    public void AggregateFile_counts_both_roles_as_messages_and_reads_the_cwd()
    {
        var path = WriteJsonl(
            UserWithBlocks("2026-08-26T10:00:00.000Z", "text"),
            Assistant("claude-opus-5", 10, 5));

        var result = StatsAggregator.AggregateFile(path, false, 0, null).Value;

        Assert.Equal(2, result.agg.Messages);
        Assert.Equal(@"C:\proj\demo", result.cwd);
    }

    [Fact]
    public void AggregateFile_takes_the_LAST_cwd_because_a_session_can_cd()
    {
        var path = WriteJsonl(
            Assistant("claude-opus-5", 10, 5, cwd: @"C:\proj\first"),
            Assistant("claude-opus-5", 10, 5, cwd: @"C:\proj\second"));

        Assert.Equal(@"C:\proj\second",
            StatsAggregator.AggregateFile(path, false, 0, null).Value.cwd);
    }

    [Fact]
    public void AggregateFile_counts_attachments_on_user_turns()
    {
        var path = WriteJsonl(
            UserWithBlocks("2026-08-26T10:00:00.000Z", "text", "image", "image", "document"));

        var agg = StatsAggregator.AggregateFile(path, false, 0, null).Value.agg;

        Assert.Equal(2, agg.ImageCount);
        Assert.Equal(1, agg.FileCount);
    }

    [Fact]
    public void AggregateFile_skips_sidechain_turns_in_a_main_transcript()
    {
        // Sub-agent turns inside a main transcript are counted in their own file, not twice here.
        var sidechain = Assistant("claude-opus-5", 999, 999);
        sidechain["isSidechain"] = true;
        var path = WriteJsonl(Assistant("claude-opus-5", 10, 5), sidechain);

        var agg = StatsAggregator.AggregateFile(path, isSubagent: false, 0, null).Value.agg;

        Assert.Equal(1, agg.Messages);
        Assert.Equal(10, agg.ModelUsage["claude-opus-5"].InputTokens);
    }

    [Fact]
    public void AggregateFile_counts_sidechain_turns_when_the_file_IS_the_subagent()
    {
        var sidechain = Assistant("claude-opus-5", 42, 7);
        sidechain["isSidechain"] = true;
        var path = WriteJsonl(sidechain);

        var agg = StatsAggregator.AggregateFile(path, isSubagent: true, 0, null).Value.agg;

        Assert.Equal(1, agg.Messages);
        Assert.Equal(42, agg.ModelUsage["claude-opus-5"].InputTokens);
    }

    [Fact]
    public void AggregateFile_adds_to_the_seed_rather_than_starting_over()
    {
        // How the cache resumes: re-reading only the bytes appended since last time, on top of
        // what was already counted. Double-counting here inflates every historical total.
        var path = WriteJsonl(Assistant("claude-opus-5", 100, 50));
        var first = StatsAggregator.AggregateFile(path, false, 0, null).Value;

        var second = StatsAggregator.AggregateFile(path, false, first.newSize, first.agg).Value;

        // Nothing was appended, so the seed comes back unchanged.
        Assert.Equal(1, second.agg.Messages);
        Assert.Equal(100, second.agg.ModelUsage["claude-opus-5"].InputTokens);
    }

    [Fact]
    public void AggregateFile_on_a_missing_file_is_null_not_a_throw()
        => Assert.Null(StatsAggregator.AggregateFile(
            Path.Combine(_dir, "nope.jsonl"), false, 0, null));

    [Fact]
    public void AggregateFile_ignores_a_line_that_is_not_valid_json()
    {
        var path = Path.Combine(_dir, "broken.jsonl");
        File.WriteAllText(path,
            Assistant("claude-opus-5", 10, 5).ToString(Newtonsoft.Json.Formatting.None) + "\n"
            + "{\"type\":\"assistant\" this is not json\n"
            + Assistant("claude-opus-5", 20, 10).ToString(Newtonsoft.Json.Formatting.None) + "\n",
            new UTF8Encoding(false));

        var agg = StatsAggregator.AggregateFile(path, false, 0, null).Value.agg;

        // The broken line is dropped; the ones around it are still counted.
        Assert.Equal(2, agg.Messages);
        Assert.Equal(30, agg.ModelUsage["claude-opus-5"].InputTokens);
    }

    [Fact]
    public void ReadCwd_finds_the_working_directory_without_parsing_the_whole_file()
    {
        var path = WriteJsonl(
            Assistant("claude-opus-5", 10, 5, cwd: @"C:\proj\demo"),
            Assistant("claude-opus-5", 10, 5, cwd: @"C:\proj\demo"));

        Assert.Equal(@"C:\proj\demo", StatsAggregator.ReadCwd(path));
    }

    [Fact]
    public void ReadCwd_on_a_file_with_no_cwd_is_empty_not_a_throw()
    {
        var noCwd = Assistant("claude-opus-5", 10, 5);
        noCwd.Remove("cwd");
        var path = WriteJsonl(noCwd);

        Assert.True(string.IsNullOrEmpty(StatsAggregator.ReadCwd(path)));
    }

    [Theory]
    // The day-bucket key. Producer and readers only agree as long as both go through DateKey —
    // change one and lookups return nothing: empty heatmap, no exception.
    [InlineData(2026, 8, 26, "2026-08-26")]
    [InlineData(2026, 1, 1, "2026-01-01")]
    [InlineData(2026, 12, 31, "2026-12-31")]
    public void DateKey_is_invariant_yyyy_MM_dd(int y, int m, int d, string expected)
        => Assert.Equal(expected, StatsAggregator.DateKey(new DateTime(y, m, d)));

    [Fact]
    public void DateKey_does_not_follow_the_thread_culture()
    {
        // An Italian locale formats dates as dd/MM/yyyy. If the key picked up the current culture
        // the buckets written on one machine would be unreadable on another.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("it-IT");
            Assert.Equal("2026-08-26", StatsAggregator.DateKey(new DateTime(2026, 8, 26)));
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    [Fact]
    public void TryParseDateKey_round_trips_what_DateKey_wrote()
    {
        var day = new DateTime(2026, 8, 26);

        Assert.True(StatsAggregator.TryParseDateKey(StatsAggregator.DateKey(day), out var parsed));
        Assert.Equal(day, parsed);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("26/08/2026")]
    [InlineData("2026-8-26")]
    [InlineData("")]
    public void TryParseDateKey_refuses_anything_DateKey_did_not_write(string key)
        => Assert.False(StatsAggregator.TryParseDateKey(key, out _));
}

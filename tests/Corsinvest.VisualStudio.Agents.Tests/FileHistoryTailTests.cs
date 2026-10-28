/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class FileHistoryTailTests
{
    private static SessionFixture Fixture(params JObject[] lines) => SessionFixture.Compact(lines);

    private static string Hook(SessionFixture fx, string name) => Path.Combine(fx.WorkingDirectory, name);

    [Fact]
    public async Task A_delta_for_the_file_is_found()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));

        var entry = await fx.Manager().FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt"));

        Assert.Equal("h@v1", entry.BackupFileName);
    }

    [Fact]
    public async Task The_newest_of_two_deltas_for_a_file_wins()
    {
        using var fx = Fixture(
            Jsonl.FileHistoryDelta("a.txt", "old@v1", 1),
            Jsonl.FileHistoryDelta("a.txt", "new@v1", 1));

        var entry = await fx.Manager().FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt"));

        Assert.Equal("new@v1", entry.BackupFileName);
    }

    [Fact]
    public async Task A_delta_for_another_file_is_not_this_files()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("b.txt", "h@v1", 1));

        Assert.Null(await fx.Manager().FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt")));
    }

    [Fact]
    public async Task A_snapshot_is_not_a_fresh_record()
    {
        // Only a delta says "tracked just now"; a snapshot repeats what earlier turns already tracked.
        using var fx = Fixture(Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "h@v2", 2)));

        Assert.Null(await fx.Manager().FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt")));
    }

    [Fact]
    public async Task A_record_written_after_a_first_look_is_found_by_the_next()
    {
        using var fx = Fixture(Jsonl.UserPrompt("u1", "go"));
        var sessions = fx.Manager();
        Assert.Null(await sessions.FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt")));

        fx.Append(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));

        Assert.NotNull(await sessions.FindRecentDeltaAsync(fx.SessionId, fx.TranscriptPath, Hook(fx, "a.txt")));
    }

    [Fact]
    public async Task The_sessions_own_transcript_is_used_when_the_hook_names_none()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));

        Assert.NotNull(await fx.Manager().FindRecentDeltaAsync(fx.SessionId, null, Hook(fx, "a.txt")));
        Assert.NotNull(await fx.Manager().FindRecentDeltaAsync(fx.SessionId, "", Hook(fx, "a.txt")));
    }

    [Fact]
    public async Task A_transcript_that_is_not_there_finds_nothing()
    {
        using var fx = Fixture();

        Assert.Null(await fx.Manager().FindRecentDeltaAsync(fx.SessionId, Path.Combine(fx.ConfigDir, "missing.jsonl"), Hook(fx, "a.txt")));
        Assert.Null(await fx.Manager().FindRecentDeltaAsync("../x", null, Hook(fx, "a.txt")));
    }

    [Fact]
    public async Task Tail_lines_are_the_whole_file_when_it_is_smaller_than_the_window()
    {
        using var fx = Fixture();
        var path = fx.WriteWork("log.txt", "one\ntwo\n\nthree\n");

        Assert.Equal(new[] { "one", "two", "three" }, await SessionManager.ReadTailLinesAsync(path, 1024));
    }

    [Fact]
    public async Task A_window_that_starts_inside_a_line_drops_that_line()
    {
        using var fx = Fixture();
        var path = fx.WriteWork("log.txt", "first line\nsecond line\nthird line\n");

        // "…line\nthird line\n": the window opens partway through the second line.
        var lines = await SessionManager.ReadTailLinesAsync(path, "d line\nthird line\n".Length);

        Assert.Equal(new[] { "third line" }, lines);
    }

    [Fact]
    public async Task A_multibyte_character_cut_by_the_window_does_not_reach_the_lines_kept()
    {
        using var fx = Fixture();
        var path = fx.WriteWork("log.txt", "café 中文\nkept über\n");
        var kept = Encoding.UTF8.GetByteCount("kept über\n");

        // Two bytes more than the last line: the window opens on the last byte of a three-byte character.
        var lines = await SessionManager.ReadTailLinesAsync(path, kept + 2);

        Assert.Equal(new[] { "kept über" }, lines);
    }

    [Fact]
    public async Task A_file_being_appended_to_can_still_be_read()
    {
        using var fx = Fixture(Jsonl.UserPrompt("u1", "go"));
        using var writer = new FileStream(fx.TranscriptPath, FileMode.Append, FileAccess.Write, FileShare.Read);

        // The CLI holds the transcript open for writing; the tail read must not need it closed.
        Assert.NotEmpty((await SessionManager.ReadTailLinesAsync(fx.TranscriptPath, 4096)).ToList());
    }
}

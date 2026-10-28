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

public class FileHistoryReaderTests
{
    private static SessionFixture Fixture(params JObject[] lines) => SessionFixture.Compact(lines);

    [Fact]
    public async Task The_first_record_for_a_file_is_its_earliest_and_the_last_its_latest()
    {
        using var fx = Fixture(
            Jsonl.FileHistoryDelta("a.txt", "h@v1", 1),
            Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "h@v2", 2)),
            Jsonl.TrackedSnapshot("u3", Jsonl.Tracked("a.txt", "h@v3", 3)));

        var tracks = await fx.Manager().ReadFileHistoryAsync(fx.SessionId);

        var track = Assert.Single(tracks).Value;
        // The content from before the session's first edit, not as of the last turn.
        Assert.Equal("h@v1", track.Earliest.BackupFileName);
        Assert.Equal("h@v3", track.Latest.BackupFileName);
    }

    [Fact]
    public async Task A_null_backup_in_the_first_record_means_the_file_did_not_exist()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("new.txt", null, 1));

        var track = Assert.Single(await fx.Manager().ReadFileHistoryAsync(fx.SessionId)).Value;

        Assert.Null(track.Earliest.BackupFileName);
    }

    [Fact]
    public async Task A_file_known_only_from_a_snapshot_starts_there()
    {
        // An earlier delta can be gone (the CLI keeps a bounded history): the oldest record that
        // survives is the best baseline there is.
        using var fx = Fixture(Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "h@v2", 2)));

        var track = Assert.Single(await fx.Manager().ReadFileHistoryAsync(fx.SessionId)).Value;

        Assert.Equal("h@v2", track.Earliest.BackupFileName);
    }

    [Fact]
    public async Task Paths_are_resolved_to_absolute_ones_whether_relative_or_not()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        var outside = Path.Combine(fx.ConfigDir, "outside", "x.txt");
        fx.Append(Jsonl.FileHistoryDelta(outside, null, 1));

        var tracks = await fx.Manager().ReadFileHistoryAsync(fx.SessionId);

        Assert.Contains(Path.Combine(fx.WorkingDirectory, "a.txt"), tracks.Keys);
        Assert.Contains(outside, tracks.Keys);
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public async Task Several_files_keep_their_own_records()
    {
        using var fx = Fixture(
            Jsonl.FileHistoryDelta("a.txt", "a@v1", 1),
            Jsonl.FileHistoryDelta("b.txt", "b@v1", 1),
            Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "a@v2", 2), Jsonl.Tracked("b.txt", "b@v2", 2)));

        var tracks = await fx.Manager().ReadFileHistoryAsync(fx.SessionId);

        Assert.Equal("a@v1", tracks[Path.Combine(fx.WorkingDirectory, "a.txt")].Earliest.BackupFileName);
        Assert.Equal("b@v2", tracks[Path.Combine(fx.WorkingDirectory, "b.txt")].Latest.BackupFileName);
    }

    [Fact]
    public async Task A_pretty_printed_transcript_reads_the_same()
    {
        var lines = new[]
        {
            Jsonl.FileHistoryDelta("a.txt", "h@v1", 1),
            Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "h@v2", 2)),
        };
        using var compact = SessionFixture.Compact(lines);
        using var pretty = SessionFixture.Pretty(lines);

        var a = (await compact.Manager().ReadFileHistoryAsync(compact.SessionId)).Values.Single();
        var b = (await pretty.Manager().ReadFileHistoryAsync(pretty.SessionId)).Values.Single();

        Assert.Equal(a.Earliest.BackupFileName, b.Earliest.BackupFileName);
        Assert.Equal(a.Latest.BackupFileName, b.Latest.BackupFileName);
    }

    [Fact]
    public async Task Other_records_and_empty_snapshots_name_no_file()
    {
        using var fx = Fixture(Jsonl.EmptySnapshot("u1"), Jsonl.UserPrompt("u1", "hello"));

        Assert.Empty(await fx.Manager().ReadFileHistoryAsync(fx.SessionId));
    }

    [Fact]
    public async Task A_session_id_that_is_not_a_plain_token_reads_nothing()
    {
        using var fx = Fixture(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));

        Assert.Empty(await fx.Manager().ReadFileHistoryAsync("../" + fx.SessionId));
        Assert.Empty(await fx.Manager().ReadFileHistoryAsync(null));
    }

    [Fact]
    public void A_backup_lives_in_the_sessions_folder_and_a_new_file_has_none()
    {
        using var fx = Fixture();
        var entry = new SessionManager.FileHistoryEntry { BackupFileName = "h@v1" };

        Assert.Equal(Path.Combine(fx.Paths.FileHistoryFolder, fx.SessionId, "h@v1"), fx.Manager().BackupPath(fx.SessionId, entry));
        Assert.Null(fx.Manager().BackupPath(fx.SessionId, new SessionManager.FileHistoryEntry()));
        Assert.Null(fx.Manager().BackupPath("../x", entry));
    }
}

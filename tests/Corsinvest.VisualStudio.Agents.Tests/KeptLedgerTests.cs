/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class KeptLedgerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public KeptLedgerTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    private KeptLedger Ledger() => new(Path.Combine(_folder, "changes"), OutputWindowLogger.Global);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, "work", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
        return path;
    }

    private string LedgerFile(string sessionId) => Path.Combine(_folder, "changes", sessionId + ".json");

    [Fact]
    public async Task A_kept_file_is_still_kept_until_it_changes()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open("s1");

        await ledger.KeepAsync(file);
        Assert.True(await ledger.IsStillKeptAsync(file));

        File.WriteAllText(file, "accepted, and then Claude touched it\n");
        Assert.False(await ledger.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task What_was_kept_is_remembered_by_the_next_ledger_of_the_same_session()
    {
        var file = Write("a.txt", "accepted\n");
        using (var first = Ledger())
        {
            first.Open("s1");
            await first.KeepAsync(file);
            await first.FlushAsync();
        }

        using var second = Ledger();
        second.Open("s1");

        Assert.True(await second.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task A_session_is_remembered_apart_from_another()
    {
        var file = Write("a.txt", "accepted\n");
        using (var first = Ledger())
        {
            first.Open("s1");
            await first.KeepAsync(file);
        }

        using var other = Ledger();
        other.Open("s2");

        Assert.False(await other.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task Closing_the_ledger_writes_what_is_waiting_without_the_delay()
    {
        var file = Write("a.txt", "accepted\n");
        var ledger = Ledger();
        ledger.Open("s1");
        await ledger.KeepAsync(file);

        ledger.Dispose();

        Assert.True(File.Exists(LedgerFile("s1")));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_just_now_is_not_recorded()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open("s1");

        // Held by another process, as a build can hold a file.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await ledger.KeepAsync(file);
        }

        Assert.False(await ledger.IsStillKeptAsync(file));
        await ledger.FlushAsync();
        Assert.False(File.Exists(LedgerFile("s1")));
    }

    [Fact]
    public async Task A_deletion_that_was_kept_is_remembered_until_the_file_is_there_again()
    {
        var file = Path.Combine(_folder, "work", "gone.txt");
        using var ledger = Ledger();
        ledger.Open("s1");

        await ledger.KeepAsync(file);
        Assert.True(await ledger.IsStillKeptAsync(file));

        await ledger.FlushAsync();
        using var next = Ledger();
        next.Open("s1");
        Assert.True(await next.IsStillKeptAsync(file));

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, "Claude wrote it again\n");
        Assert.False(await next.IsStillKeptAsync(file));
    }

    [Theory]
    [InlineData("{ \"version\": \"x\", \"kept\": {} }")]
    [InlineData("{ \"version\": {}, \"kept\": {} }")]
    [InlineData("{ \"version\": [1], \"kept\": {} }")]
    [InlineData("{ \"version\": 1, \"kept\": [] }")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("")]
    public async Task A_ledger_of_a_shape_it_does_not_know_is_the_same_as_none_and_does_not_throw(string content)
    {
        var file = Write("a.txt", "accepted\n");
        Directory.CreateDirectory(Path.Combine(_folder, "changes"));
        File.WriteAllText(LedgerFile("s1"), content);
        using var ledger = Ledger();

        ledger.Open("s1");

        Assert.False(await ledger.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task Two_ledgers_of_one_session_do_not_drop_each_other_s_files()
    {
        var one = Write("a.txt", "one\n");
        var two = Write("b.txt", "two\n");
        using var first = Ledger();
        using var second = Ledger();
        // Neither has read anything of the other's.
        first.Open("s1");
        second.Open("s1");

        await first.KeepAsync(one);
        await first.FlushAsync();
        await second.KeepAsync(two);
        await second.FlushAsync();

        using var later = Ledger();
        later.Open("s1");
        Assert.True(await later.IsStillKeptAsync(one));
        Assert.True(await later.IsStillKeptAsync(two));
        // And no temporary file is left beside the ledger's.
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "changes"), "*.tmp"));
    }

    [Fact]
    public async Task A_session_that_was_open_recently_is_not_swept_though_nothing_was_kept_in_it_for_long()
    {
        var file = Write("a.txt", "accepted\n");
        var ledger = Ledger();
        ledger.Open("s1");
        await ledger.KeepAsync(file);
        await ledger.FlushAsync();
        File.SetLastWriteTimeUtc(LedgerFile("s1"), DateTime.UtcNow - TimeSpan.FromDays(40));

        ledger.Dispose();
        KeptLedger.Sweep(Path.Combine(_folder, "changes"), TimeSpan.FromDays(30), OutputWindowLogger.Global);

        Assert.True(File.Exists(LedgerFile("s1")));
    }

    [Fact]
    public async Task A_file_that_is_not_the_ledger_s_is_the_same_as_no_ledger()
    {
        var file = Write("a.txt", "accepted\n");
        Directory.CreateDirectory(Path.Combine(_folder, "changes"));
        File.WriteAllText(LedgerFile("s1"), "this is not json {");
        using var ledger = Ledger();

        ledger.Open("s1");

        Assert.False(await ledger.IsStillKeptAsync(file));
        // And it can be written over.
        await ledger.KeepAsync(file);
        await ledger.FlushAsync();
        using var again = Ledger();
        again.Open("s1");
        Assert.True(await again.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task A_version_the_ledger_does_not_know_is_ignored()
    {
        var file = Write("a.txt", "accepted\n");
        Directory.CreateDirectory(Path.Combine(_folder, "changes"));
        File.WriteAllText(LedgerFile("s1"),
            "{ \"version\": 99, \"kept\": { " + Newtonsoft.Json.JsonConvert.ToString(file) + ": \"1:abc\" } }");
        using var ledger = Ledger();

        ledger.Open("s1");

        Assert.False(await ledger.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task What_is_kept_before_the_chat_has_a_session_goes_into_the_session_it_gets()
    {
        var file = Write("a.txt", "accepted\n");
        using (var first = Ledger())
        {
            first.Open(null);
            await first.KeepAsync(file);
            first.Open("s1");
            await first.FlushAsync();
        }

        using var second = Ledger();
        second.Open("s1");

        Assert.True(await second.IsStillKeptAsync(file));
    }

    [Fact]
    public async Task Leaving_a_session_leaves_what_was_kept_in_it_and_coming_back_reads_it_again()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open("s1");
        await ledger.KeepAsync(file);

        ledger.Open(null);
        Assert.False(await ledger.IsStillKeptAsync(file));

        ledger.Open("s1");
        Assert.True(await ledger.IsStillKeptAsync(file));
    }


    [Fact]
    public async Task A_session_id_that_is_not_safe_in_a_path_is_kept_in_memory_and_never_written()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open(@"..\..\elsewhere");

        await ledger.KeepAsync(file);
        await ledger.FlushAsync();

        Assert.True(await ledger.IsStillKeptAsync(file));
        Assert.False(Directory.Exists(Path.Combine(_folder, "changes")) && Directory.EnumerateFiles(Path.Combine(_folder, "changes")).Any());
        // Where the id would lead if it were used as it is: two folders above the ledger's, beside this test's own.
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_folder, "changes", @"..\..\elsewhere.json"))));
    }

    [Fact]
    public async Task The_stamp_tells_apart_files_of_the_same_length()
    {
        var one = Write("a.txt", "ab");
        var two = Write("b.txt", "ac");

        Assert.NotEqual(await KeptLedger.StampAsync(one), await KeptLedger.StampAsync(two));
        Assert.Equal(await KeptLedger.StampAsync(one), await KeptLedger.StampAsync(one));
        Assert.Equal(KeptLedger.Absent, await KeptLedger.StampAsync(Path.Combine(_folder, "work", "missing.txt")));
        Assert.Null(await KeptLedger.StampAsync(one, maxBytes: 1));
    }

    [Fact]
    public void Sweeping_removes_the_files_nobody_has_touched_for_long_and_only_those()
    {
        var folder = Path.Combine(_folder, "changes");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.json");
        var oldTemp = Path.Combine(folder, "old.json.0123456789abcdef.tmp");
        var recent = Path.Combine(folder, "recent.json");
        File.WriteAllText(old, "{}");
        File.WriteAllText(oldTemp, "{");
        File.WriteAllText(recent, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(40));
        File.SetLastWriteTimeUtc(oldTemp, DateTime.UtcNow - TimeSpan.FromDays(40));

        KeptLedger.Sweep(folder, TimeSpan.FromDays(30), OutputWindowLogger.Global);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldTemp));
        Assert.True(File.Exists(recent));
    }
}

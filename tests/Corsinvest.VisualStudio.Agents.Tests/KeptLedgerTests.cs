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
        Assert.True(ledger.IsStillKept(file));

        File.WriteAllText(file, "accepted, and then Claude touched it\n");
        Assert.False(ledger.IsStillKept(file));
    }

    [Fact]
    public async Task What_was_kept_is_remembered_by_the_next_ledger_of_the_same_session()
    {
        var file = Write("a.txt", "accepted\n");
        using (var first = Ledger())
        {
            first.Open("s1");
            await first.KeepAsync(file);
            first.Flush();
        }

        using var second = Ledger();
        second.Open("s1");

        Assert.True(second.IsStillKept(file));
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

        Assert.False(other.IsStillKept(file));
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
    public async Task A_file_that_cannot_be_read_is_not_recorded()
    {
        using var ledger = Ledger();
        ledger.Open("s1");
        var missing = Path.Combine(_folder, "work", "missing.txt");

        await ledger.KeepAsync(missing);

        Assert.False(ledger.IsStillKept(missing));
        ledger.Flush();
        Assert.False(File.Exists(LedgerFile("s1")));
    }

    [Fact]
    public async Task A_file_that_is_not_the_ledger_s_is_the_same_as_no_ledger()
    {
        var file = Write("a.txt", "accepted\n");
        Directory.CreateDirectory(Path.Combine(_folder, "changes"));
        File.WriteAllText(LedgerFile("s1"), "this is not json {");
        using var ledger = Ledger();

        ledger.Open("s1");

        Assert.False(ledger.IsStillKept(file));
        // And it can be written over.
        await ledger.KeepAsync(file);
        ledger.Flush();
        using var again = Ledger();
        again.Open("s1");
        Assert.True(again.IsStillKept(file));
    }

    [Fact]
    public void A_version_the_ledger_does_not_know_is_ignored()
    {
        var file = Write("a.txt", "accepted\n");
        Directory.CreateDirectory(Path.Combine(_folder, "changes"));
        File.WriteAllText(LedgerFile("s1"),
            "{ \"version\": 99, \"kept\": { " + Newtonsoft.Json.JsonConvert.ToString(file) + ": \"1:abc\" } }");
        using var ledger = Ledger();

        ledger.Open("s1");

        Assert.False(ledger.IsStillKept(file));
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
            first.Flush();
        }

        using var second = Ledger();
        second.Open("s1");

        Assert.True(second.IsStillKept(file));
    }

    [Fact]
    public async Task Leaving_a_session_leaves_what_was_kept_in_it_and_coming_back_reads_it_again()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open("s1");
        await ledger.KeepAsync(file);

        ledger.Open(null);
        Assert.False(ledger.IsStillKept(file));

        ledger.Open("s1");
        Assert.True(ledger.IsStillKept(file));
    }

    [Fact]
    public async Task Forgetting_a_file_removes_it()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open("s1");
        await ledger.KeepAsync(file);

        ledger.Forget(file);

        Assert.False(ledger.IsStillKept(file));
    }

    [Fact]
    public async Task A_session_id_that_is_not_safe_in_a_path_is_kept_in_memory_and_never_written()
    {
        var file = Write("a.txt", "accepted\n");
        using var ledger = Ledger();
        ledger.Open(@"..\..\elsewhere");

        await ledger.KeepAsync(file);
        ledger.Flush();

        Assert.True(ledger.IsStillKept(file));
        Assert.False(Directory.Exists(Path.Combine(_folder, "changes")) && Directory.EnumerateFiles(Path.Combine(_folder, "changes")).Any());
        Assert.False(File.Exists(Path.Combine(_folder, "elsewhere.json")));
    }

    [Fact]
    public void The_stamp_tells_apart_files_of_the_same_length()
    {
        var one = Write("a.txt", "ab");
        var two = Write("b.txt", "ac");

        Assert.NotEqual(KeptLedger.Stamp(one), KeptLedger.Stamp(two));
        Assert.Equal(KeptLedger.Stamp(one), KeptLedger.Stamp(one));
        Assert.Null(KeptLedger.Stamp(Path.Combine(_folder, "work", "missing.txt")));
    }

    [Fact]
    public void Sweeping_removes_the_files_nobody_has_touched_for_long_and_only_those()
    {
        var folder = Path.Combine(_folder, "changes");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.json");
        var recent = Path.Combine(folder, "recent.json");
        File.WriteAllText(old, "{}");
        File.WriteAllText(recent, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(40));

        KeptLedger.Sweep(folder, TimeSpan.FromDays(30), OutputWindowLogger.Global);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}

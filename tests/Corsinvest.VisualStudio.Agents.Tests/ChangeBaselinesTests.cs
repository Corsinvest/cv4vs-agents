/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Linq;
using Xunit;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class ChangeBaselinesTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ChangeBaselines _baselines;

    public ChangeBaselinesTests()
    {
        _root = Path.Combine(_scratch, "baselines");
        Directory.CreateDirectory(_scratch);
        _baselines = new ChangeBaselines(_root, OutputWindowLogger.Global);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    private string Source(string name, string content)
    {
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task A_copy_has_the_content_the_file_had_and_keeps_its_name()
    {
        var file = Source("Program.cs", "class A { }");

        var copy = await _baselines.CaptureAsync("s1", file);

        Assert.True(copy.Owned);
        Assert.EndsWith("~Program.cs", copy.Path);
        Assert.Equal("class A { }", File.ReadAllText(copy.Path));
        Assert.StartsWith(Path.Combine(_root, "s1"), copy.Path);
    }

    [Fact]
    public async Task A_copy_is_a_snapshot_that_later_edits_of_the_file_do_not_reach()
    {
        var file = Source("a.txt", "before");
        var copy = await _baselines.CaptureAsync("s1", file);

        File.WriteAllText(file, "after");

        Assert.Equal("before", File.ReadAllText(copy.Path));
    }

    [Fact]
    public async Task A_file_that_is_not_there_has_an_empty_baseline_and_no_copy()
    {
        var copy = await _baselines.CaptureAsync("s1", Path.Combine(_scratch, "absent.txt"));

        Assert.True(copy.IsEmpty);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Two_files_with_the_same_name_do_not_share_a_copy()
    {
        Directory.CreateDirectory(Path.Combine(_scratch, "one"));
        Directory.CreateDirectory(Path.Combine(_scratch, "two"));
        var a = Source(Path.Combine("one", "x.txt"), "a");
        var b = Source(Path.Combine("two", "x.txt"), "b");

        var copyA = await _baselines.CaptureAsync("s1", a);
        var copyB = await _baselines.CaptureAsync("s1", b);

        Assert.NotEqual(copyA.Path, copyB.Path);
        Assert.Equal("a", File.ReadAllText(copyA.Path));
        Assert.Equal("b", File.ReadAllText(copyB.Path));
    }

    [Fact]
    public async Task Two_copies_of_the_same_file_do_not_share_a_name_so_one_cannot_overwrite_or_delete_the_other()
    {
        var file = Source("a.txt", "one");
        var first = await _baselines.CaptureAsync("s1", file);
        File.WriteAllText(file, "two");
        var second = await _baselines.CaptureAsync("s1", file);

        Assert.NotEqual(first.Path, second.Path);
        Assert.EndsWith("~a.txt", second.Path);

        _baselines.Release(first);

        Assert.False(File.Exists(first.Path));
        Assert.Equal("two", File.ReadAllText(second.Path));
    }

    [Fact]
    public async Task Releasing_deletes_a_copy_it_owns()
    {
        var copy = await _baselines.CaptureAsync("s1", Source("a.txt", "x"));

        _baselines.Release(copy);

        Assert.False(File.Exists(copy.Path));
    }

    [Fact]
    public void Releasing_never_touches_a_cli_backup_or_anything_outside_its_folder()
    {
        var backup = Source("backup@v1", "the CLI's");
        var outside = Source("outside.txt", "not ours");

        _baselines.Release(BaselineRef.Cli(backup));
        // Owned by claim, but not under the folder: still left alone.
        _baselines.Release(BaselineRef.Own(outside));
        _baselines.Release(BaselineRef.None);
        _baselines.Release(BaselineRef.Empty);

        Assert.True(File.Exists(backup));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void A_session_id_that_is_not_a_plain_token_gets_a_shared_folder_not_a_traversal()
    {
        Assert.Equal(Path.Combine(_root, "unsaved"), _baselines.FolderFor("../../etc"));
        Assert.Equal(Path.Combine(_root, "unsaved"), _baselines.FolderFor(null));
        Assert.Equal(Path.Combine(_root, "abc-123"), _baselines.FolderFor("abc-123"));
    }

    [Fact]
    public void Sweeping_removes_old_session_folders_and_keeps_recent_ones()
    {
        var old = Path.Combine(_root, "old");
        var recent = Path.Combine(_root, "recent");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        _baselines.Sweep(TimeSpan.FromDays(14));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void A_folder_whose_owner_marker_is_held_open_is_not_swept_however_old_and_is_once_it_is_let_go()
    {
        var live = Path.Combine(_root, "live");
        Directory.CreateDirectory(live);
        var marker = Path.Combine(live, GitSnapshot.OwnerMarker);
        File.WriteAllText(marker, "");
        var old = DateTime.UtcNow.AddDays(-30);
        File.SetCreationTimeUtc(marker, old);
        File.SetLastWriteTimeUtc(marker, old);
        Directory.SetCreationTimeUtc(live, old);
        Directory.SetLastWriteTimeUtc(live, old);

        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            _baselines.Sweep(TimeSpan.FromDays(14));
            Assert.True(Directory.Exists(live));
        }

        _baselines.Sweep(TimeSpan.FromDays(14));
        Assert.False(Directory.Exists(live));
    }

    [Fact]
    public void A_folder_with_a_copy_made_recently_in_it_is_kept_whatever_its_own_time_says()
    {
        // The folder's time moves only when an entry is added or removed, and a copy keeps the write time
        // of its source: a chat open for weeks would look abandoned to another Visual Studio starting up.
        var busy = Path.Combine(_root, "busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "abc~a.txt"), "x");
        Directory.SetCreationTimeUtc(busy, DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddDays(-30));

        _baselines.Sweep(TimeSpan.FromDays(14));

        Assert.True(Directory.Exists(busy));
    }

    [Fact]
    public void Sweeping_a_folder_that_is_not_there_does_nothing()
    {
        _baselines.Sweep(TimeSpan.FromDays(14));

        Assert.Empty(Directory.GetDirectories(_scratch).Where(d => d.EndsWith("baselines")));
    }

    [Fact]
    public void The_short_hash_ignores_case_and_tells_paths_apart()
    {
        Assert.Equal(ChangeBaselines.ShortHash(@"C:\A\x.txt"), ChangeBaselines.ShortHash(@"c:\a\X.TXT"));
        Assert.NotEqual(ChangeBaselines.ShortHash(@"C:\A\x.txt"), ChangeBaselines.ShortHash(@"C:\B\x.txt"));
        Assert.Equal(8, ChangeBaselines.ShortHash("anything").Length);
    }
}

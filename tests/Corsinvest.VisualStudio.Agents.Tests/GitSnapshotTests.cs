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

public sealed class GitSnapshotTests : IDisposable
{
    private readonly GitRepoFixture _repo;
    private readonly string _private;
    private GitSnapshot _snapshot;

    public GitSnapshotTests()
    {
        _repo = GitRepoFixture.Available ? new GitRepoFixture() : null;
        _private = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _snapshot?.Dispose();
        _repo?.Dispose();
        try { Directory.Delete(_private, true); }
        catch (Exception) { /* absent when nothing was taken */ }
    }

    private async Task<GitSnapshot> OpenAsync(string directory = null)
    {
        _snapshot = await GitSnapshot.OpenAsync(new GitRunner(GitExe.Resolve(), OutputWindowLogger.Global),
                                                directory ?? _repo.Root, _private, OutputWindowLogger.Global);
        return _snapshot;
    }

    /// <summary>What differs between two trees, as "status:path" in the order git gives them.</summary>
    private static async Task<string[]> DiffAsync(GitSnapshot snapshot, GitTree before, GitTree after)
        => (await snapshot.DiffAsync(before.Oid, after.Oid)).Select(c => c.Status + ":" + c.RelativePath).ToArray();

    [Fact]
    public async Task A_folder_outside_any_repository_has_no_snapshot()
    {
        if (_repo is null) { return; }
        var outside = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Null(await OpenAsync(outside));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task The_snapshot_knows_the_root_of_the_working_tree_from_a_subfolder()
    {
        if (_repo is null) { return; }
        _repo.Write("src/a.txt", "a\n");
        var snapshot = await OpenAsync(_repo.Path_("src"));

        Assert.Equal(Path.GetFullPath(_repo.Root).TrimEnd('\\'), Path.GetFullPath(snapshot.TopLevel).TrimEnd('\\'), ignoreCase: true);
    }

    [Fact]
    public async Task A_snapshot_follows_tracked_and_untracked_files_and_not_ignored_ones()
    {
        if (_repo is null) { return; }
        _repo.Write(".gitignore", "obj/\n*.log\n");
        _repo.Write("tracked.txt", "tracked\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var before = await snapshot.TakeAsync();

        _repo.Write("tracked.txt", "changed\n");
        _repo.Write("new.txt", "untracked\n");
        _repo.Write("obj/out.dll", "built\n");
        _repo.Write("debug.log", "noise\n");
        var after = await snapshot.TakeAsync();

        Assert.True(before.Complete);
        Assert.True(after.Complete);
        Assert.Equal(new[] { "A:new.txt", "M:tracked.txt" }, await DiffAsync(snapshot, before, after));
    }

    [Fact]
    public async Task The_latest_tree_is_the_newest_one_taken()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        Assert.Null(snapshot.LatestTree);

        var first = await snapshot.TakeAsync();
        _repo.Write("a.txt", "b\n");
        var second = await snapshot.TakeAsync();

        Assert.NotEqual(first.Oid, second.Oid);
        Assert.Equal(second.Oid, snapshot.LatestTree.Oid);
    }

    [Fact]
    public async Task A_tree_that_did_not_change_has_the_same_id_and_no_differences()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();

        var first = await snapshot.TakeAsync();
        var second = await snapshot.TakeAsync();

        Assert.Equal(first.Oid, second.Oid);
        Assert.Empty(await snapshot.DiffAsync(first.Oid, second.Oid));
    }

    [Fact]
    public async Task Nothing_is_written_into_the_users_repository()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var before = _repo.Git("count-objects", "-v");
        var indexBefore = File.GetLastWriteTimeUtc(_repo.Path_(".git/index"));
        _repo.Write("brand-new.txt", "never committed\n");
        var snapshot = await OpenAsync();

        await snapshot.TakeAsync();

        Assert.Equal(before, _repo.Git("count-objects", "-v"));
        Assert.Equal(indexBefore, File.GetLastWriteTimeUtc(_repo.Path_(".git/index")));
        Assert.Equal("?? brand-new.txt", _repo.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_blob_is_the_file_as_it_was_when_the_snapshot_was_taken()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "before\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();
        _repo.Write("a.txt", "after\n");
        var second = await snapshot.TakeAsync();

        var change = Assert.Single(await snapshot.DiffAsync(first.Oid, second.Oid));

        var target = Path.Combine(_private, "restored.txt");
        Assert.True(await snapshot.WriteBlobAsync(change.OldOid, change.RelativePath, target));
        Assert.Equal("before\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_burst_of_requests_is_served_by_a_run_that_began_after_the_last_of_them_was_made()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();
        Assert.Equal(1, snapshot.Runs);

        _repo.Write("b.txt", "b\n");
        var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => snapshot.TakeAsync()));

        // Not eight scans: the one that was under way when most were asked for cannot have seen b.txt, so
        // one more is taken, and serves everything that waited behind it.
        Assert.True(snapshot.Runs <= 3, "runs: " + snapshot.Runs);
        Assert.All(burst, tree => Assert.NotNull(tree));
        Assert.Contains("A:b.txt", await DiffAsync(snapshot, first, burst.Last()));
    }

    [Fact]
    public async Task What_was_changed_added_and_removed_between_two_snapshots_is_told_apart()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\n");
        _repo.Write("b.txt", "stays\n");
        _repo.Write("c.txt", "goes\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        _repo.Write("a.txt", "two\n");
        _repo.Write("d.txt", "added\n");
        File.Delete(_repo.Path_("c.txt"));
        var second = await snapshot.TakeAsync();

        Assert.Equal(new[] { "M:a.txt", "D:c.txt", "A:d.txt" }, await DiffAsync(snapshot, first, second));
    }

    [Fact]
    public async Task A_tracked_file_is_followed_whatever_a_pattern_says()
    {
        if (_repo is null) { return; }
        _repo.Write("tracked.log", "committed\n");
        _repo.Commit();
        _repo.Write(".gitignore", "*.log\n");
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        _repo.Write("tracked.log", "edited\n");
        _repo.Write("other.log", "ignored\n");
        var second = await snapshot.TakeAsync();

        Assert.Equal(new[] { "M:tracked.log" }, await DiffAsync(snapshot, first, second));
    }

    [Fact]
    public async Task A_name_is_kept_as_the_index_has_it_when_the_file_on_disk_changed_case()
    {
        if (_repo is null) { return; }
        // Git on Windows ignores case, so a rename that only changes it is not one to the index.
        _repo.Git("config", "core.ignorecase", "true");
        _repo.Write("Readme.md", "one\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        File.Move(_repo.Path_("Readme.md"), _repo.Path_("README.tmp"));
        File.Move(_repo.Path_("README.tmp"), _repo.Path_("README.md"));
        File.WriteAllText(_repo.Path_("README.md"), "two\n");
        var second = await snapshot.TakeAsync();

        // One file, changed. Not a new README.md beside a vanished Readme.md.
        Assert.Equal(new[] { "M:Readme.md" }, await DiffAsync(snapshot, first, second));
    }

    [Fact]
    public async Task The_path_of_a_change_is_made_absolute_under_the_root()
    {
        if (_repo is null) { return; }
        var snapshot = await OpenAsync();

        Assert.Equal(_repo.Path_("src/app/a.cs"), snapshot.Absolute("src/app/a.cs"), ignoreCase: true);
    }

    [Fact]
    public async Task A_repository_nested_in_the_tree_is_one_entry_that_is_not_a_file()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        var nested = _repo.Path_("vendor/lib");
        Directory.CreateDirectory(nested);
        _repo.GitIn(nested, "init", "-q");
        _repo.GitIn(nested, "config", "user.email", "test@example.com");
        _repo.GitIn(nested, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(nested, "code.c"), "int x;\n");
        _repo.GitIn(nested, "add", "-A");
        _repo.GitIn(nested, "commit", "-q", "-m", "inner");
        var second = await snapshot.TakeAsync();

        var change = Assert.Single(await snapshot.DiffAsync(first.Oid, second.Oid));
        Assert.Equal("vendor/lib", change.RelativePath);
        Assert.True(change.IsSubmodule);
        Assert.False(change.NewIsFile);
    }

    [Fact]
    public async Task A_file_another_program_holds_open_is_named_and_left_out_and_the_rest_is_followed()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        _repo.Write("ok.txt", "fine\n");
        var locked = _repo.Write("sub dir/locked file.txt", "busy\n");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var second = await snapshot.TakeAsync();

        Assert.True(second.Usable);
        Assert.False(second.Complete);
        Assert.True(second.Skips("sub dir/locked file.txt"));
        Assert.False(second.Skips("ok.txt"));
        // Good enough to compare with, so it is the newest tree.
        Assert.Equal(second.Oid, snapshot.LatestTree.Oid);
        Assert.Equal(new[] { "A:ok.txt" }, await DiffAsync(snapshot, first, second));
    }

    [Fact]
    public async Task Disposing_removes_the_private_store()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        await snapshot.TakeAsync();
        Assert.True(Directory.Exists(_private));

        snapshot.Dispose();

        Assert.False(Directory.Exists(_private));
    }

    [Fact]
    public async Task A_run_that_fails_hands_nothing_to_those_waiting_behind_it_and_above_all_not_the_picture_before()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        var good = await snapshot.TakeAsync();
        // The next run cannot even make its folder: a file is where the objects go.
        ChangeBaselines.DeleteTree(Path.Combine(_private, "objects"));
        File.WriteAllText(Path.Combine(_private, "objects"), "in the way");

        var outcomes = new System.Collections.Generic.List<GitTree>();
        var failures = 0;
        foreach (var task in Enumerable.Range(0, 3).Select(_ => snapshot.TakeAsync()).ToList())
        {
            try { outcomes.Add(await task); }
            catch (IOException) { failures++; }
        }

        Assert.True(failures >= 1);
        Assert.DoesNotContain(good, outcomes);
    }

    [Fact]
    public async Task A_store_that_is_in_use_is_marked_so_and_a_disposed_one_is_gone()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        await snapshot.TakeAsync();
        var marker = Path.Combine(_private, GitSnapshot.OwnerMarker);
        Assert.True(File.Exists(marker));

        // Nobody else can have it open exclusively while the store lives: that is how a sweep tells.
        Assert.Throws<IOException>(() => new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None).Dispose());

        snapshot.Dispose();
        Assert.False(Directory.Exists(_private));
    }

    [Fact]
    public async Task What_git_said_when_it_would_not_open_a_folder_is_handed_back()
    {
        if (_repo is null) { return; }
        var outside = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string said = null;
            var runner = new GitRunner(GitExe.Resolve(), OutputWindowLogger.Global);

            var snapshot = await GitSnapshot.OpenAsync(runner, outside, _private, OutputWindowLogger.Global, text => said = text);

            Assert.Null(snapshot);
            Assert.Contains("not a git repository", said);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task A_picture_asked_for_after_disposing_makes_nothing()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        snapshot.Dispose();

        var tree = await snapshot.TakeAsync();

        Assert.Null(tree);
        Assert.False(Directory.Exists(_private));
    }

    [Fact]
    public async Task A_linked_worktree_is_snapshotted_from_its_own_folder()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "main\n");
        _repo.Commit();
        var linked = Path.Combine(_repo.Scratch, "linked");
        _repo.Git("worktree", "add", "-q", "-b", "other", linked);
        var snapshot = await OpenAsync(linked);
        var first = await snapshot.TakeAsync();

        File.WriteAllText(Path.Combine(linked, "b.txt"), "only here\n");
        var second = await snapshot.TakeAsync();

        Assert.Equal(Path.GetFullPath(linked).TrimEnd('\\'), Path.GetFullPath(snapshot.TopLevel).TrimEnd('\\'), ignoreCase: true);
        Assert.Equal(new[] { "A:b.txt" }, await DiffAsync(snapshot, first, second));
        // The main working tree's own index is left as it was.
        Assert.Equal("", _repo.Git("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_lock_left_by_a_killed_git_does_not_stop_the_next_picture()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        await snapshot.TakeAsync();
        File.WriteAllText(Path.Combine(_private, "index.lock"), "");

        var tree = await snapshot.TakeAsync();

        Assert.NotNull(tree);
        Assert.False(File.Exists(Path.Combine(_private, "index.lock")));
    }

    [Fact]
    public async Task A_damaged_private_index_is_started_again_from_nothing()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();
        File.WriteAllText(Path.Combine(_private, "index"), "this is not an index");
        _repo.Write("b.txt", "b\n");

        var second = await snapshot.TakeAsync();

        Assert.NotNull(second);
        Assert.True(second.Complete);
        Assert.Equal(new[] { "A:b.txt" }, await DiffAsync(snapshot, first, second));
    }

    [Theory]
    [InlineData(@"C:\", null, true)]
    [InlineData(@"D:\", null, true)]
    [InlineData(@"C:\Users\jane", @"C:\Users\jane", true)]
    [InlineData(@"C:\Users\jane\", @"C:\Users\jane", true)]
    [InlineData(@"c:\users\JANE", @"C:\Users\jane", true)]
    [InlineData(@"C:\Users\jane\source\repos\app", @"C:\Users\jane", false)]
    [InlineData(@"D:\work", @"C:\Users\jane", false)]
    public void A_repository_that_is_a_whole_drive_or_profile_is_too_broad_to_follow(string topLevel, string home, bool expected)
        => Assert.Equal(expected, GitSnapshot.IsTooBroad(topLevel, home));

    [Fact]
    public void Raw_output_is_read_into_changes_with_their_modes_and_ids()
    {
        var raw = ":100644 100755 aaa bbb M\0src/a.cs\0:000000 100644 000 ccc A\0new file.txt\0:100644 000000 ddd 000 D\0gone.txt\0";

        var changes = GitSnapshot.ParseRaw(raw);

        Assert.Equal(new[] { "M:src/a.cs", "A:new file.txt", "D:gone.txt" }, changes.Select(c => c.Status + ":" + c.RelativePath));
        Assert.Equal("aaa", changes[0].OldOid);
        Assert.Equal("bbb", changes[0].NewOid);
        Assert.True(changes[0].OldIsFile);
        Assert.True(changes[0].NewIsFile);
        Assert.False(changes[1].OldIsFile);
        Assert.False(changes[2].NewIsFile);
    }

    [Fact]
    public void A_link_and_a_nested_repository_are_not_files()
    {
        var raw = ":120000 120000 aaa bbb M\0link\0:160000 160000 ccc ddd M\0vendor/lib\0";

        var changes = GitSnapshot.ParseRaw(raw);

        Assert.False(changes[0].NewIsFile);
        Assert.False(changes[0].IsSubmodule);
        Assert.True(changes[1].IsSubmodule);
    }

    [Fact]
    public void No_output_is_no_changes()
    {
        Assert.Empty(GitSnapshot.ParseRaw(""));
    }

    [Fact]
    public void The_files_git_skipped_are_read_from_its_two_error_lines_each()
    {
        var stderr = "error: open(\"sub dir/locked file.txt\"): Permission denied\n" +
                     "error: unable to index file 'sub dir/locked file.txt'\n" +
                     "error: open(\"b.txt\"): Permission denied\n" +
                     "error: unable to index file 'b.txt'\n";

        var ok = GitSnapshot.TrySkippedPaths(stderr, out var skipped);

        Assert.True(ok);
        Assert.Equal(new[] { "b.txt", "sub dir/locked file.txt" }, skipped.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void Warnings_and_hints_beside_the_skipped_files_are_advice_and_do_not_spoil_the_list()
    {
        // A repository nested in this one makes git say this on every add.
        var stderr = "warning: adding embedded git repository: vendor/lib\n" +
                     "hint: You've added another git repository inside your current repository.\n" +
                     "hint:\n" +
                     "hint: \tgit submodule add <url> vendor/lib\n" +
                     "warning: in the working copy of 'a.txt', LF will be replaced by CRLF the next time Git touches it\n" +
                     "error: open(\"locked.txt\"): Permission denied\n" +
                     "error: unable to index file 'locked.txt'\n";

        Assert.True(GitSnapshot.TrySkippedPaths(stderr, out var skipped));
        Assert.Equal(new[] { "locked.txt" }, skipped);
    }

    [Theory]
    [InlineData("")]
    [InlineData("warning: only advice, and nothing skipped\n")]
    [InlineData("error: short read while indexing a.txt\n")]
    [InlineData("error: open(\"a.txt\"): Permission denied\nfatal: something nobody expected\n")]
    [InlineData("some words nobody expected\n")]
    public void A_list_with_a_line_that_is_not_understood_is_not_a_list(string stderr)
    {
        Assert.False(GitSnapshot.TrySkippedPaths(stderr, out var skipped));
        Assert.Null(skipped);
    }

    [Fact]
    public void A_tree_that_skips_a_path_by_any_case_says_nothing_about_it()
    {
        var tree = new GitTree("abc", new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sub/File.txt" });

        Assert.True(tree.Usable);
        Assert.False(tree.Complete);
        Assert.True(tree.Skips("sub/file.txt"));
        Assert.False(tree.Skips("other.txt"));
    }

    [Fact]
    public void A_tree_with_unknown_skips_is_unusable_and_says_nothing_about_anything()
    {
        var tree = new GitTree("abc", null);

        Assert.False(tree.Usable);
        Assert.False(tree.Complete);
        Assert.True(tree.Skips("anything.txt"));
    }
}

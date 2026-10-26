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
    public async Task A_snapshot_records_tracked_and_untracked_files_and_not_ignored_ones()
    {
        if (_repo is null) { return; }
        _repo.Write(".gitignore", "obj/\n*.log\n");
        _repo.Write("tracked.txt", "tracked\n");
        _repo.Commit();
        _repo.Write("new.txt", "untracked\n");
        _repo.Write("obj/out.dll", "built\n");
        _repo.Write("debug.log", "noise\n");
        var snapshot = await OpenAsync();

        var tree = await snapshot.TakeAsync();

        Assert.False(string.IsNullOrEmpty(tree));
        Assert.Equal(tree, snapshot.LatestTree);
        var blobs = await snapshot.ListBlobsAsync(tree, new[] { "tracked.txt", "new.txt", "obj/out.dll", "debug.log", ".gitignore" });
        Assert.Contains("tracked.txt", blobs.Keys);
        Assert.Contains("new.txt", blobs.Keys);
        Assert.Contains(".gitignore", blobs.Keys);
        Assert.DoesNotContain("obj/out.dll", blobs.Keys);
        Assert.DoesNotContain("debug.log", blobs.Keys);
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
        var tree = await snapshot.TakeAsync();

        _repo.Write("a.txt", "after\n");

        var blob = (await snapshot.ListBlobsAsync(tree, new[] { "a.txt" }))["a.txt"];
        var target = Path.Combine(_private, "restored.txt");
        Assert.True(await snapshot.WriteBlobAsync(blob.Oid, "a.txt", target));
        Assert.Equal("before\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_file_that_was_never_there_is_absent_from_the_answer()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        var snapshot = await OpenAsync();
        var tree = await snapshot.TakeAsync();

        var blobs = await snapshot.ListBlobsAsync(tree, new[] { "a.txt", "never.txt", "dir/never.txt" });

        Assert.Equal(new[] { "a.txt" }, blobs.Keys.ToArray());
    }

    [Fact]
    public async Task A_second_snapshot_sees_what_changed_after_the_first()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\n");
        _repo.Write("b.txt", "stays\n");
        _repo.Commit();
        var snapshot = await OpenAsync();
        var first = await snapshot.TakeAsync();

        _repo.Write("a.txt", "two\n");
        _repo.Write("c.txt", "added\n");
        File.Delete(_repo.Path_("b.txt"));
        var second = await snapshot.TakeAsync();

        Assert.NotEqual(first, second);
        var changed = await snapshot.ChangedBetweenAsync(first, second);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, changed.OrderBy(c => c).ToArray());
    }

    [Fact]
    public async Task Ignored_paths_are_told_from_the_ones_to_keep()
    {
        if (_repo is null) { return; }
        _repo.Write(".gitignore", "obj/\n*.log\n");
        _repo.Write("kept.txt", "x\n");
        var snapshot = await OpenAsync();

        var ignored = await snapshot.IgnoredAsync(new[] { "obj/a.dll", "x.log", "kept.txt", "src/kept.cs" });

        Assert.Equal(new[] { "obj/a.dll", "x.log" }, ignored.OrderBy(p => p).ToArray());
    }

    [Fact]
    public async Task A_tracked_file_is_never_ignored_whatever_a_pattern_says()
    {
        if (_repo is null) { return; }
        _repo.Write("tracked.log", "committed\n");
        _repo.Commit();
        _repo.Write(".gitignore", "*.log\n");
        var snapshot = await OpenAsync();

        var ignored = await snapshot.IgnoredAsync(new[] { "tracked.log", "other.log" });

        Assert.Equal(new[] { "other.log" }, ignored.ToArray());
    }

    [Fact]
    public async Task A_path_is_made_relative_to_the_root_and_a_file_outside_it_has_none()
    {
        if (_repo is null) { return; }
        var snapshot = await OpenAsync();

        Assert.Equal("src/app/a.cs", snapshot.Relative(_repo.Path_("src/app/a.cs")));
        Assert.Null(snapshot.Relative(Path.Combine(_repo.Scratch, "elsewhere", "a.cs")));
    }

    [Fact]
    public async Task A_file_inside_a_nested_repository_has_no_relative_path()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        Directory.CreateDirectory(_repo.Path_("vendor/lib/.git"));
        _repo.Write("vendor/lib/code.c", "int x;\n");
        var snapshot = await OpenAsync();

        Assert.Null(snapshot.Relative(_repo.Path_("vendor/lib/code.c")));
        Assert.Equal("a.txt", snapshot.Relative(_repo.Path_("a.txt")));
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
        File.WriteAllText(Path.Combine(linked, "b.txt"), "only here\n");
        var snapshot = await OpenAsync(linked);

        var tree = await snapshot.TakeAsync();

        Assert.Equal(Path.GetFullPath(linked).TrimEnd('\\'), Path.GetFullPath(snapshot.TopLevel).TrimEnd('\\'), ignoreCase: true);
        var blobs = await snapshot.ListBlobsAsync(tree, new[] { "a.txt", "b.txt" });
        Assert.Contains("a.txt", blobs.Keys);
        Assert.Contains("b.txt", blobs.Keys);
        // The main working tree's own index is left as it was.
        Assert.Equal("", _repo.Git("status", "--porcelain").Trim());
    }
}

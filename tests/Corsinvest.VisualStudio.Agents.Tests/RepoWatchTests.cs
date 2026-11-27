/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Threading;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class RepoWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RepoWatch _watch;

    public RepoWatchTests()
    {
        Directory.CreateDirectory(_root);
        _watch = new RepoWatch(_root, OutputWindowLogger.Global);
    }

    public void Dispose()
    {
        _watch.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    private string Path_(string name) => Path.Combine(_root, name);

    private bool Sees(long mark, TimeSpan? within = null)
        => SpinWait.SpinUntil(() => _watch.ChangedSince(mark), within ?? TimeSpan.FromSeconds(10));

    [Fact]
    public void A_change_after_a_mark_is_seen_from_that_mark_and_not_from_a_later_one()
    {
        var first = _watch.Mark();
        _watch.Touch(Path_("a.txt"));
        var second = _watch.Mark();

        Assert.True(_watch.ChangedSince(first));
        Assert.False(_watch.ChangedSince(second));

        _watch.Touch(Path_("b.txt"));

        Assert.True(_watch.ChangedSince(second));
    }

    [Fact]
    public void What_happens_inside_dot_git_is_not_a_change()
    {
        var mark = _watch.Mark();

        _watch.Touch(Path_(Path.Combine(".git", "index")));
        _watch.Touch(Path_(Path.Combine(".git", "objects", "ab", "cdef")));
        _watch.Touch(Path_(".git"));

        Assert.False(_watch.ChangedSince(mark));
    }

    [Fact]
    public void Visual_Studios_own_cache_is_not_a_change()
    {
        var mark = _watch.Mark();

        _watch.Touch(Path_(Path.Combine(".vs", "app", "v17", ".suo")));
        _watch.Touch(Path_(".vs"));

        Assert.False(_watch.ChangedSince(mark));
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData(".vscode")]
    [InlineData(".vsconfig")]
    public void A_name_that_only_starts_like_one_of_them_is_a_change(string name)
    {
        var mark = _watch.Mark();

        _watch.Touch(Path_(name));

        Assert.True(_watch.ChangedSince(mark));
    }

    [Fact]
    public void A_real_write_under_the_folder_is_noticed()
    {
        var mark = _watch.Mark();

        File.WriteAllText(Path_("real.txt"), "hello");

        Assert.True(Sees(mark));
    }

    [Fact]
    public void A_real_write_inside_a_subfolder_and_a_delete_are_noticed()
    {
        Directory.CreateDirectory(Path_("sub"));
        File.WriteAllText(Path_(Path.Combine("sub", "old.txt")), "x");
        // Let what the setup raised settle, so what follows is what is being asked about.
        Thread.Sleep(500);
        var mark = _watch.Mark();

        File.Delete(Path_(Path.Combine("sub", "old.txt")));

        Assert.True(Sees(mark));
    }

    [Fact]
    public void A_real_write_inside_a_dot_git_folder_is_not_noticed_and_a_visible_one_is()
    {
        Directory.CreateDirectory(Path_(".git"));
        Thread.Sleep(300);
        var mark = _watch.Mark();

        File.WriteAllText(Path_(Path.Combine(".git", "HEAD")), "ref");
        Thread.Sleep(500);
        Assert.False(_watch.ChangedSince(mark));

        File.WriteAllText(Path_("visible.txt"), "x");
        Assert.True(Sees(mark));
    }
}

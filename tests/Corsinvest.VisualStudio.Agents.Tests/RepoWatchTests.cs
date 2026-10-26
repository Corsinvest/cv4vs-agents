/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Linq;
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

    [Fact]
    public void A_path_touched_after_a_mark_is_reported_for_that_mark_and_not_for_a_later_one()
    {
        var first = _watch.Mark();
        _watch.Touch(Path_("a.txt"));
        var second = _watch.Mark();
        _watch.Touch(Path_("b.txt"));

        Assert.Equal(new[] { Path_("a.txt"), Path_("b.txt") }, _watch.PathsSince(first).OrderBy(p => p).ToArray());
        Assert.Equal(new[] { Path_("b.txt") }, _watch.PathsSince(second).ToArray());
        Assert.True(_watch.ChangedSince(first));
        Assert.True(_watch.ChangedSince(second));
        Assert.False(_watch.ChangedSince(_watch.Mark()));
    }

    [Fact]
    public void The_same_path_touched_twice_is_one_path_at_its_latest_moment()
    {
        _watch.Touch(Path_("a.txt"));
        var between = _watch.Mark();
        _watch.Touch(Path_("a.txt"));

        Assert.Equal(new[] { Path_("a.txt") }, _watch.PathsSince(between).ToArray());
    }

    [Fact]
    public void What_happens_inside_dot_git_is_not_a_change()
    {
        var mark = _watch.Mark();

        _watch.Touch(Path_(Path.Combine(".git", "index")));
        _watch.Touch(Path_(Path.Combine(".git", "objects", "ab", "cdef")));
        _watch.Touch(Path_(".git"));

        Assert.False(_watch.ChangedSince(mark));
        Assert.Empty(_watch.PathsSince(mark));
        // A folder that merely has git in its name is another matter.
        _watch.Touch(Path_(".gitignore"));
        Assert.True(_watch.ChangedSince(mark));
    }

    [Fact]
    public void Past_the_cap_the_list_is_dropped_and_the_overflow_declared()
    {
        using var small = new RepoWatch(_root, OutputWindowLogger.Global, maxPaths: 3);
        var mark = small.Mark();

        for (var i = 0; i < 5; i++) { small.Touch(Path_("f" + i)); }

        Assert.True(small.OverflowedSince(mark));
        Assert.False(small.OverflowedSince(small.Mark()));
    }

    [Fact]
    public void A_real_write_under_the_folder_is_noticed()
    {
        var mark = _watch.Mark();

        File.WriteAllText(Path_("real.txt"), "hello");

        Assert.True(SpinWait.SpinUntil(() => _watch.PathsSince(mark).Contains(Path_("real.txt")), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_real_write_inside_a_subfolder_and_a_delete_are_noticed()
    {
        Directory.CreateDirectory(Path_("sub"));
        File.WriteAllText(Path_(Path.Combine("sub", "old.txt")), "x");
        var mark = _watch.Mark();

        File.WriteAllText(Path_(Path.Combine("sub", "new.txt")), "y");
        File.Delete(Path_(Path.Combine("sub", "old.txt")));

        Assert.True(SpinWait.SpinUntil(() =>
        {
            var paths = _watch.PathsSince(mark);
            return paths.Contains(Path_(Path.Combine("sub", "new.txt"))) && paths.Contains(Path_(Path.Combine("sub", "old.txt")));
        }, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void A_real_write_inside_a_dot_git_folder_is_not_reported()
    {
        Directory.CreateDirectory(Path_(".git"));
        var mark = _watch.Mark();

        File.WriteAllText(Path_(Path.Combine(".git", "HEAD")), "ref");
        File.WriteAllText(Path_("visible.txt"), "x");

        Assert.True(SpinWait.SpinUntil(() => _watch.PathsSince(mark).Contains(Path_("visible.txt")), TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(_watch.PathsSince(mark), p => p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar));
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class PaneRootTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private IDisposable _junction;

    public void Dispose()
    {
        // The junction first: see Junction.
        _junction?.Dispose();
        try { Directory.Delete(_base, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    // The mapping on its own, with spellings that need no disk.
    private static readonly PaneRoot Linked = new(@"C:\Users\me\Dev\repo", @"D:\repo");

    [Fact]
    public void A_path_under_the_real_folder_comes_out_under_the_link()
    {
        Assert.Equal(@"C:\Users\me\Dev\repo\src\a.cs", Linked.ToLogical(@"D:\repo\src\a.cs"));
    }

    [Fact]
    public void The_real_folder_itself_comes_out_as_the_link()
    {
        Assert.Equal(@"C:\Users\me\Dev\repo", Linked.ToLogical(@"D:\repo"));
    }

    [Fact]
    public void Case_does_not_matter_for_the_folder_and_is_kept_for_the_rest()
    {
        Assert.Equal(@"C:\Users\me\Dev\repo\Src\A.cs", Linked.ToLogical(@"d:\REPO\Src\A.cs"));
    }

    [Fact]
    public void A_folder_that_only_starts_the_same_is_not_under_it()
    {
        Assert.Equal(@"D:\repo-two\a.cs", Linked.ToLogical(@"D:\repo-two\a.cs"));
    }

    [Theory]
    [InlineData(@"D:\other\a.cs")]
    [InlineData(@"E:\repo\a.cs")]
    [InlineData(@"C:\Users\me\Dev\repo\a.cs")]
    [InlineData(@"D:")]
    public void A_path_outside_the_real_folder_is_left_alone(string path)
    {
        Assert.Equal(path, Linked.ToLogical(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_is_a_path_to_map(string path)
    {
        Assert.Equal(path, Linked.ToLogical(path));
    }

    [Fact]
    public void Two_spellings_of_one_name_are_no_link()
    {
        var same = new PaneRoot(@"D:\repo", @"d:\REPO\");

        Assert.False(same.IsLinked);
        Assert.Equal(@"D:\repo\a.cs", same.ToLogical(@"D:\repo\a.cs"));
    }

    [Fact]
    public void None_maps_nothing()
    {
        Assert.False(PaneRoot.None.IsLinked);
        Assert.Equal(@"D:\repo\a.cs", PaneRoot.None.ToLogical(@"D:\repo\a.cs"));
    }

    [Fact]
    public void A_whole_drive_behind_a_folder_is_mapped_too()
    {
        // C:\Users\me\Dev is a junction to the root of D:.
        var drive = new PaneRoot(@"C:\Users\me\Dev", @"D:\");

        Assert.Equal(@"C:\Users\me\Dev\GitHub\repo\a.cs", drive.ToLogical(@"D:\GitHub\repo\a.cs"));
        // The drive's own root keeps its separator: it is still a folder path, now through the link.
        Assert.Equal(@"C:\Users\me\Dev\", drive.ToLogical(@"D:\"));
    }

    // Against the file system.

    private string MakeLinkedWorkDir()
    {
        var real = Path.Combine(_base, "real");
        Directory.CreateDirectory(Path.Combine(real, "work"));
        var link = Path.Combine(_base, "link");
        _junction = Junction.Create(link, real);
        return Path.Combine(link, "work");
    }

    [Fact]
    public void A_folder_reached_through_a_junction_is_known_by_its_real_path_too()
    {
        var work = MakeLinkedWorkDir();

        var root = PaneRoot.Of(work);

        Assert.True(root.IsLinked);
        Assert.Equal(work, root.Logical, ignoreCase: true);
        Assert.EndsWith(@"\real\work", root.Real, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_named_by_the_real_path_comes_out_under_the_junction()
    {
        var work = MakeLinkedWorkDir();
        var root = PaneRoot.Of(work);

        var mapped = root.ToLogical(Path.Combine(root.Real, "src", "a.cs"));

        Assert.Equal(Path.Combine(work, "src", "a.cs"), mapped, ignoreCase: true);
    }

    [Fact]
    public void A_folder_with_no_link_in_it_maps_nothing()
    {
        var plain = Path.Combine(_base, "plain");
        Directory.CreateDirectory(plain);
        var root = PaneRoot.Of(plain);
        var file = Path.Combine(plain, "a.cs");

        // Short and long forms of the same name (8.3, as a build agent's temp folder may have) count as two spellings, which maps the
        // long one onto the short one: harmless. A path in the spelling the folder was given in is never changed.
        Assert.Equal(file, root.ToLogical(file));
    }

    [Fact]
    public void A_folder_that_is_not_there_has_no_real_path()
    {
        var root = PaneRoot.Of(Path.Combine(_base, "missing"));

        Assert.False(root.IsLinked);
        Assert.Equal(@"D:\x\a.cs", root.ToLogical(@"D:\x\a.cs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_folder_has_no_real_path(string folder)
    {
        Assert.False(PaneRoot.Of(folder).IsLinked);
    }
}

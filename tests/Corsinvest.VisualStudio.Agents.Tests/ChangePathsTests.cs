/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class ChangePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public ChangePathsTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    [Fact]
    public void A_relative_path_is_resolved_against_the_base()
    {
        Assert.Equal(Path.Combine(_root, "src", "a.cs"), ChangePaths.Normalize(Path.Combine("src", "a.cs"), _root));
    }

    [Fact]
    public void An_absolute_path_is_kept_and_cleaned_up()
    {
        var path = Path.Combine(_root, "src", "..", "src", "a.cs");

        Assert.Equal(Path.Combine(_root, "src", "a.cs"), ChangePaths.Normalize(path, "ignored"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_path(string path)
    {
        Assert.Null(ChangePaths.Normalize(path, _root));
    }

    [Fact]
    public void A_relative_path_with_no_base_is_not_a_path()
    {
        Assert.Null(ChangePaths.Normalize("a.cs", null));
    }

    [Fact]
    public void A_tracking_path_under_the_working_directory_wins_when_its_folder_exists()
    {
        var resolved = ChangePaths.Resolve(Path.Combine("src", "a.cs"), Path.Combine(_root, "elsewhere"), _root);

        Assert.Equal(Path.Combine(_root, "src", "a.cs"), resolved);
    }

    [Fact]
    public void The_real_directory_is_used_when_the_relative_path_points_nowhere()
    {
        // The working directory is not the one the CLI used (a cd moved it): the relative path
        // leads to a folder that is not there, the recorded real directory does.
        var other = Path.Combine(_root, "other");
        var resolved = ChangePaths.Resolve(Path.Combine("nope", "a.cs"), Path.Combine(_root, "src"), other);

        Assert.Equal(Path.Combine(_root, "src", "a.cs"), resolved);
    }

    [Fact]
    public void An_absolute_tracking_path_is_taken_as_it_is()
    {
        var outside = Path.Combine(_root, "elsewhere", "x.txt");

        Assert.Equal(outside, ChangePaths.Resolve(outside, Path.Combine(_root, "src"), _root));
    }

    [Fact]
    public void A_hook_path_matches_a_record_under_either_spelling_and_any_case()
    {
        var hook = Path.Combine(_root, "SRC", "A.cs");

        Assert.True(ChangePaths.Matches(hook, Path.Combine("src", "a.cs"), null, _root));
        Assert.False(ChangePaths.Matches(hook, "unrelated", Path.Combine(_root, "src"), _root));
        Assert.True(ChangePaths.Matches(hook, Path.Combine("src", "a.cs"), Path.Combine(_root, "elsewhere"), _root));
    }

    [Fact]
    public void A_record_matches_through_its_real_directory_when_the_tracking_path_is_another_name()
    {
        var hook = Path.Combine(_root, "src", "a.cs");

        // A junction: the tool spelled the path through the link, the CLI recorded the target.
        Assert.True(ChangePaths.Matches(hook, "a.cs", Path.Combine(_root, "src"), Path.Combine(_root, "link")));
    }

    [Fact]
    public void A_different_file_does_not_match()
    {
        Assert.False(ChangePaths.Matches(Path.Combine(_root, "src", "a.cs"), Path.Combine("src", "b.cs"), null, _root));
    }

    // The working directory is C:\Users\me\Dev\repo, a junction to D:\repo: Claude Code 2.1.314 and git name the files by D:\repo.
    private static readonly PaneRoot Linked = new(@"C:\Users\me\Dev\repo", @"D:\repo");
    private const string Logical = @"C:\Users\me\Dev\repo";

    [Fact]
    public void A_path_reported_by_the_real_folder_is_normalised_into_the_working_directorys_spelling()
    {
        Assert.Equal(@"C:\Users\me\Dev\repo\src\a.cs", ChangePaths.Normalize(@"D:\repo\src\a.cs", Logical, Linked));
        Assert.Equal(@"C:\Users\me\Dev\repo\src\a.cs", ChangePaths.Normalize(@"src\a.cs", @"D:\repo", Linked));
    }

    [Fact]
    public void Without_a_root_a_path_is_only_normalised()
    {
        Assert.Equal(@"D:\repo\src\a.cs", ChangePaths.Normalize(@"D:\repo\src\a.cs", Logical, null));
    }

    [Fact]
    public void A_record_with_an_absolute_real_tracking_path_is_listed_under_the_link()
    {
        Assert.Equal(@"C:\Users\me\Dev\repo\b.cs", ChangePaths.Resolve(@"D:\repo\b.cs", null, Logical, Linked));
    }

    [Fact]
    public void A_hook_in_the_real_spelling_matches_a_record_that_names_no_real_directory()
    {
        // No realParentDir to fall back on: only the working directory's spelling can say it is the same file.
        Assert.True(ChangePaths.Matches(@"D:\repo\a.cs", "a.cs", null, Logical, Linked));
        Assert.False(ChangePaths.Matches(@"D:\repo\a.cs", "a.cs", null, Logical));
    }

    [Fact]
    public void A_hook_under_the_link_matches_a_record_the_cli_made_by_the_real_path()
    {
        Assert.True(ChangePaths.Matches(@"C:\Users\me\Dev\repo\a.cs", @"D:\repo\a.cs", null, Logical, Linked));
    }
}

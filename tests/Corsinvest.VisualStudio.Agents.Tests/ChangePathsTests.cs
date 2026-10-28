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
}

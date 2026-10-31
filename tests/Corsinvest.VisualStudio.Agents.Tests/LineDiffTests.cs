/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class LineDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string Numbered(string prefix, int count)
        => Lines(Enumerable.Range(0, count).Select(i => prefix + i).ToArray());

    [Fact]
    public void Identical_text_has_no_changes()
    {
        var counts = LineDiff.Count(Lines("a", "b", "c"), Lines("a", "b", "c"));

        Assert.Equal(0, counts.Added);
        Assert.Equal(0, counts.Deleted);
        Assert.False(counts.Approximate);
    }

    [Fact]
    public void Replacing_fourteen_lines_with_one_counts_one_added_and_fourteen_deleted()
    {
        // The case the tool cards already got right: the gross change, not the net.
        var counts = LineDiff.Count(Numbered("line ", 14), Lines("just one"));

        Assert.Equal(1, counts.Added);
        Assert.Equal(14, counts.Deleted);
    }

    [Fact]
    public void A_changed_line_counts_once_each_way()
    {
        var counts = LineDiff.Count(Lines("a", "b", "c"), Lines("a", "B", "c"));

        Assert.Equal(1, counts.Added);
        Assert.Equal(1, counts.Deleted);
    }

    [Fact]
    public void A_line_inserted_in_the_middle_is_one_added()
    {
        var counts = LineDiff.Count(Lines("a", "b", "c", "d"), Lines("a", "b", "x", "c", "d"));

        Assert.Equal(1, counts.Added);
        Assert.Equal(0, counts.Deleted);
    }

    [Fact]
    public void An_empty_before_counts_every_line_as_added()
    {
        var counts = LineDiff.Count("", Numbered("n", 5));

        Assert.Equal(5, counts.Added);
        Assert.Equal(0, counts.Deleted);
    }

    [Fact]
    public void An_empty_after_counts_every_line_as_deleted()
    {
        var counts = LineDiff.Count(Numbered("n", 5), "");

        Assert.Equal(0, counts.Added);
        Assert.Equal(5, counts.Deleted);
    }

    [Fact]
    public void Line_endings_are_not_content()
    {
        // A CRLF file read against an LF backup must not read as every line changed.
        var counts = LineDiff.Count("a\nb\nc\n", "a\r\nb\r\nc\r\n");

        Assert.Equal(0, counts.Added);
        Assert.Equal(0, counts.Deleted);
    }

    [Fact]
    public void A_final_newline_does_not_add_a_line()
    {
        var counts = LineDiff.Count("a\nb", "a\nb\n");

        Assert.Equal(0, counts.Added);
        Assert.Equal(0, counts.Deleted);
    }

    [Fact]
    public void A_block_moved_elsewhere_is_counted_as_removed_and_added()
    {
        var before = Lines("a", "b", "c", "x", "y", "z");
        var after = Lines("x", "y", "z", "a", "b", "c");

        var counts = LineDiff.Count(before, after);

        // The exact search keeps the longer common run and re-adds the other: three each way.
        Assert.Equal(3, counts.Added);
        Assert.Equal(3, counts.Deleted);
        Assert.False(counts.Approximate);
    }

    [Fact]
    public void Text_with_nothing_in_common_past_the_search_limit_is_approximated()
    {
        var counts = LineDiff.Count(Numbered("a", 6000), Numbered("b", 6000));

        Assert.True(counts.Approximate);
        Assert.Equal(6000, counts.Added);
        Assert.Equal(6000, counts.Deleted);
    }

    [Fact]
    public void The_approximation_still_ignores_the_unchanged_ends()
    {
        var before = Lines("head") + Numbered("a", 6000) + Lines("tail");
        var after = Lines("head") + Numbered("b", 6000) + Lines("tail");

        var counts = LineDiff.Count(before, after);

        Assert.True(counts.Approximate);
        Assert.Equal(6000, counts.Added);
        Assert.Equal(6000, counts.Deleted);
    }

    [Fact]
    public void Lines_splits_on_newline_and_drops_the_carriage_return()
    {
        Assert.Equal(new[] { "a", "b" }, LineDiff.Lines("a\r\nb\r\n"));
        Assert.Equal(new[] { "a", "" , "b" }, LineDiff.Lines("a\n\nb"));
        Assert.Empty(LineDiff.Lines(""));
        Assert.Empty(LineDiff.Lines(null));
    }

    [Fact]
    public void A_last_line_with_no_newline_is_a_line_and_loses_a_carriage_return_it_ends_with()
    {
        Assert.Equal(new[] { "a", "b" }, LineDiff.Lines("a\nb"));
        Assert.Equal(new[] { "a", "b" }, LineDiff.Lines("a\nb\r"));
        Assert.Equal(new[] { "a" }, LineDiff.Lines("a\r"));
        Assert.Equal(new[] { "" }, LineDiff.Lines("\r"));
    }
}

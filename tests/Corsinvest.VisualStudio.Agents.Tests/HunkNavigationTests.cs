/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class HunkNavigationTests
{
    private static readonly int[] Starts = { 10, 40, 90 };

    [Fact]
    public void No_hunks_means_nowhere_to_go()
    {
        Assert.Equal(-1, HunkNavigation.NextAfter(null, 5));
        Assert.Equal(-1, HunkNavigation.NextAfter(new int[0], 5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(25, 1)]
    [InlineData(40, 2)]
    [InlineData(89, 2)]
    public void The_next_hunk_is_the_first_that_starts_after_the_caret(int caret, int expected)
    {
        Assert.Equal(expected, HunkNavigation.NextAfter(Starts, caret));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(500)]
    public void Past_the_last_hunk_it_wraps_to_the_first(int caret)
    {
        Assert.Equal(0, HunkNavigation.NextAfter(Starts, caret));
    }

    [Fact]
    public void With_one_hunk_the_next_is_that_hunk()
    {
        Assert.Equal(0, HunkNavigation.NextAfter(new[] { 10 }, 10));
        Assert.Equal(0, HunkNavigation.NextAfter(new[] { 10 }, 0));
    }
}

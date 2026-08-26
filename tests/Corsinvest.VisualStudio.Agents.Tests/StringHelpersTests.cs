/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The shorteners behind menu rows, tab captions and list items.
/// <para>A session title falls back to the last prompt, which is a whole message — newlines and
/// all. One of those in a MenuItem breaks the row instead of wrapping, and the break looks like a
/// layout bug rather than a string that was never collapsed.</para></summary>
public class StringHelpersTests
{
    [Theory]
    [InlineData("short", 10, "short")]
    // Exactly at the limit is not truncated: the ellipsis would claim something was dropped.
    [InlineData("exactly-10", 10, "exactly-10")]
    [InlineData("this is far too long", 7, "this is…")]
    [InlineData("", 5, "")]
    [InlineData(null, 5, null)]
    public void Truncate_only_cuts_past_the_limit(string s, int max, string expected)
        => Assert.Equal(expected, StringHelpers.Truncate(s, max));

    [Fact]
    public void Truncate_marks_the_cut_so_the_reader_knows_there_was_more()
    {
        var result = StringHelpers.Truncate(new string('x', 600));

        Assert.EndsWith("…", result);
        Assert.Equal(501, result.Length); // 500 kept plus the one-char ellipsis
    }

    [Theory]
    // Newlines become spaces, and each line is trimmed on the way.
    [InlineData("first\nsecond", 100, "first second")]
    [InlineData("first\r\nsecond", 100, "first second")]
    [InlineData("  first  \n  second  ", 100, "first second")]
    // Blank lines collapse away rather than leaving double spaces.
    [InlineData("first\n\n\nsecond", 100, "first second")]
    // Collapse happens BEFORE the cut: truncating first would land inside line one and drop the
    // rest silently, which is the bug this function exists to avoid.
    [InlineData("first line\nsecond line", 12, "first line s…")]
    [InlineData("", 10, "")]
    [InlineData("   ", 10, "   ")]
    [InlineData(null, 10, null)]
    public void ToSingleLine_collapses_then_truncates(string s, int max, string expected)
        => Assert.Equal(expected, StringHelpers.ToSingleLine(s, max));

    [Theory]
    [InlineData("one\ntwo\nthree", 3)]
    // Empty lines are not counted; a trailing newline does not invent one.
    [InlineData("one\n\ntwo", 2)]
    [InlineData("one\n", 1)]
    [InlineData("single", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void NonEmptyLineCount_counts_the_lines_that_hold_something(string text, int expected)
        => Assert.Equal(expected, StringHelpers.NonEmptyLineCount(text));
}

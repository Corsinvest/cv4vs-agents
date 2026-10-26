/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class BaselineTextTests
{
    [Fact]
    public void A_text_with_no_line_break_has_no_dominant_ending()
    {
        Assert.Null(BaselineText.DominantLineEnding(null));
        Assert.Null(BaselineText.DominantLineEnding(""));
        Assert.Null(BaselineText.DominantLineEnding("one line"));
    }

    [Fact]
    public void The_dominant_ending_is_the_one_there_is_more_of()
    {
        Assert.Equal("\r\n", BaselineText.DominantLineEnding("a\r\nb\r\nc\n"));
        Assert.Equal("\n", BaselineText.DominantLineEnding("a\nb\nc\r\n"));
        Assert.Equal("\n", BaselineText.DominantLineEnding("a\nb\n"));
    }

    [Fact]
    public void A_tie_goes_to_crlf_as_the_comparison_window_does()
    {
        Assert.Equal("\r\n", BaselineText.DominantLineEnding("a\r\nb\n"));
    }

    [Fact]
    public void A_lone_carriage_return_is_not_a_line_break()
    {
        Assert.Null(BaselineText.DominantLineEnding("a\rb\rc"));
        Assert.Equal("a\rb\r\nc\r\n", BaselineText.WithLineEnding("a\rb\nc\n", "\r\n"));
    }

    [Fact]
    public void Every_line_break_is_written_as_asked_and_none_is_doubled()
    {
        Assert.Equal("a\r\nb\r\nc\r\n", BaselineText.WithLineEnding("a\nb\r\nc\n", "\r\n"));
        Assert.Equal("a\nb\nc\n", BaselineText.WithLineEnding("a\nb\r\nc\r\n", "\n"));
        Assert.Equal("a\r\nb\r\n", BaselineText.WithLineEnding("a\r\nb\r\n", "\r\n"));
    }

    [Fact]
    public void No_ending_to_match_leaves_the_text_alone()
    {
        Assert.Equal("a\r\nb\n", BaselineText.WithLineEnding("a\r\nb\n", null));
        Assert.Equal("", BaselineText.WithLineEnding("", "\r\n"));
        Assert.Null(BaselineText.WithLineEnding(null, "\r\n"));
    }

    // The mark is an internal type and a test method has to be public: it travels by name.
    private static ChangeEvaluator.Mark Parse(string name) => (ChangeEvaluator.Mark)Enum.Parse(typeof(ChangeEvaluator.Mark), name);

    [Theory]
    [InlineData("None", 0, new byte[0])]
    [InlineData("Utf8", 3, new byte[] { 0xEF, 0xBB, 0xBF })]
    [InlineData("Utf16LittleEndian", 2, new byte[] { 0xFF, 0xFE })]
    [InlineData("Utf16BigEndian", 2, new byte[] { 0xFE, 0xFF })]
    public void The_encoding_starts_with_the_mark_it_was_asked_for(string name, int length, byte[] expected)
    {
        var mark = Parse(name);
        var bytes = BaselineText.Encode("héllo\n", mark);

        Assert.Equal(expected, bytes.Take(length).ToArray());
        Assert.Equal(mark, ChangeEvaluator.ByteOrderMark(bytes));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Utf8")]
    [InlineData("Utf16LittleEndian")]
    [InlineData("Utf16BigEndian")]
    public void What_is_written_reads_back_the_same(string name)
    {
        const string text = "one\r\ntwo é €\r\n\r\nthree";

        var back = ChangeEvaluator.DecodeStrict(BaselineText.Encode(text, Parse(name)));

        Assert.Equal(text, back);
    }

    [Fact]
    public void An_empty_text_is_just_the_mark()
    {
        Assert.Empty(BaselineText.Encode("", ChangeEvaluator.Mark.None));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, BaselineText.Encode("", ChangeEvaluator.Mark.Utf8));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("one line", false)]
    [InlineData("a\nb\n", false)]
    [InlineData("a\r\nb\r\n", false)]
    [InlineData("a\rb\r\n", false)]
    [InlineData("a\r\nb\n", true)]
    [InlineData("a\nb\r\nc\n", true)]
    public void A_mixture_of_crlf_and_lone_lf_is_told_and_nothing_else_is(string text, bool mixed)
    {
        Assert.Equal(mixed, BaselineText.HasMixedLineEndings(text));
    }

    [Fact]
    public void A_utf32_file_is_not_written_back_from_text()
    {
        Assert.Throws<NotSupportedException>(() => BaselineText.Encode("a", ChangeEvaluator.Mark.Utf32LittleEndian));
    }
}

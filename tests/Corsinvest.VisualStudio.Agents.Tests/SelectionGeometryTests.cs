/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class SelectionGeometryTests
{
    // "alpha\nbeta\ngamma" — starts 0, 6, 11; ends (excl. newline) 5, 10, 16
    private static readonly int[] Starts = [0, 6, 11];
    private static readonly int[] Ends = [5, 10, 16];

    [Fact]
    public void SingleLineSelection_ReportsOneBasedLineAndZeroBasedColumn()
    {
        var r = SelectionGeometry.Compute(1, 4, Starts, Ends);
        Assert.Equal(1, r.StartLine);
        Assert.Equal(1, r.StartCol);
        Assert.Equal(1, r.EndLine);
        Assert.Equal(4, r.EndCol);
    }

    [Fact]
    public void DragToStartOfNextLine_StepsBackToTheLineThatEndsTheSelection()
    {
        // Selection ends at offset 6 = start of line 2, which holds none of it.
        var r = SelectionGeometry.Compute(0, 6, Starts, Ends);
        Assert.Equal(1, r.StartLine);
        Assert.Equal(1, r.EndLine);
        Assert.Equal(5, r.EndCol);
    }

    [Fact]
    public void BareCaretAtColumnZero_IsLeftAlone()
    {
        var r = SelectionGeometry.Compute(6, 6, Starts, Ends);
        Assert.Equal(2, r.StartLine);
        Assert.Equal(2, r.EndLine);
        Assert.Equal(0, r.StartCol);
        Assert.Equal(0, r.EndCol);
    }

    [Fact]
    public void MultiLineSelection_SpansBothLines()
    {
        var r = SelectionGeometry.Compute(1, 8, Starts, Ends);
        Assert.Equal(1, r.StartLine);
        Assert.Equal(2, r.EndLine);
        Assert.Equal(1, r.StartCol);
        Assert.Equal(2, r.EndCol);
    }

    [Fact]
    public void StepBackDoesNotDependOnWhetherTheTextIsWorthReporting()
    {
        // A drag over blank lines ends at the start of a line it holds nothing of, exactly like a
        // drag over code. The step-back is keyed on the span, never on what the text turned out to
        // be — the two were conflated once and a whitespace-only drag reported a line too many.
        var r = SelectionGeometry.Compute(6, 11, Starts, Ends);
        Assert.Equal(2, r.StartLine);
        Assert.Equal(2, r.EndLine);
    }

    [Fact]
    public void EndColumn_IsClampedToTheLineEnd()
    {
        // After a step-back the raw end offset sits past the line it landed on.
        var r = SelectionGeometry.Compute(0, 11, Starts, Ends);
        Assert.Equal(2, r.EndLine);
        // "beta" is four characters, so one past its last is 4 — the raw end offset (11,
        // the start of line 3) would overshoot it.
        Assert.Equal(4, r.EndCol);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("\t", true)]
    [InlineData("  \r\n ", true)]   // whitespace only
    [InlineData("a", true)]          // one character left after trim: an accidental drag
    [InlineData("ab", false)]
    [InlineData("  ab  ", false)]
    public void IsEffectivelyEmpty_TreatsWhitespaceAndSingleCharAsNoSelection(string text, bool expected)
        => Assert.Equal(expected, SelectionGeometry.IsEffectivelyEmpty(text));
}

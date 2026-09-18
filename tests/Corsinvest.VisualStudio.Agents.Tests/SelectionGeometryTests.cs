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
    public void SingleLineSelection_StaysOnOneLine()
    {
        var r = SelectionGeometry.Compute(1, 4, Starts, Ends);
        Assert.Equal(0, r.StartLineOffset);
        Assert.Equal(1, r.StartCol);
        Assert.Equal(0, r.EndLineOffset);
        Assert.Equal(4, r.EndCol);
    }

    [Fact]
    public void DragToStartOfNextLine_StepsBackToTheLineThatEndsTheSelection()
    {
        // Selection ends at offset 6 = start of line 2, which holds none of it.
        var r = SelectionGeometry.Compute(0, 6, Starts, Ends);
        Assert.Equal(0, r.StartLineOffset);
        Assert.Equal(0, r.EndLineOffset);
        Assert.Equal(5, r.EndCol);
    }

    [Fact]
    public void BareCaretAtColumnZero_IsLeftAlone()
    {
        var r = SelectionGeometry.Compute(6, 6, Starts, Ends);
        Assert.Equal(1, r.StartLineOffset);
        Assert.Equal(1, r.EndLineOffset);
        Assert.Equal(0, r.StartCol);
        Assert.Equal(0, r.EndCol);
    }

    [Fact]
    public void MultiLineSelection_SpansBothLines()
    {
        var r = SelectionGeometry.Compute(1, 8, Starts, Ends);
        Assert.Equal(0, r.StartLineOffset);
        Assert.Equal(1, r.EndLineOffset);
        Assert.Equal(1, r.StartCol);
        Assert.Equal(2, r.EndCol);
    }

    [Fact]
    public void StepBackDoesNotDependOnWhetherTheTextIsWorthReporting()
    {
        // A blank-line drag overshoots exactly like a drag over code: the step-back is keyed on
        // the span, not on whether the text is worth reporting.
        var r = SelectionGeometry.Compute(6, 11, Starts, Ends);
        Assert.Equal(1, r.StartLineOffset);
        Assert.Equal(1, r.EndLineOffset);
    }

    [Fact]
    public void EndColumn_IsClampedToTheLineEnd()
    {
        // After a step-back the raw end offset sits past the line it landed on.
        var r = SelectionGeometry.Compute(0, 11, Starts, Ends);
        Assert.Equal(1, r.EndLineOffset);
        // "beta" is four characters; the raw end offset 11 would overshoot.
        Assert.Equal(4, r.EndCol);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("\t", true)]
    [InlineData("  \r\n ", true)]   // whitespace only, however much of it
    [InlineData("a", false)]         // one real character is a real selection: double-clicking `i`
    [InlineData(" a ", false)]       // …and the trim must not talk it away
    [InlineData("ab", false)]
    [InlineData("  ab  ", false)]
    public void IsEffectivelyEmpty_IsAboutWhitespace_NotAboutLength(string text, bool expected)
        => Assert.Equal(expected, SelectionGeometry.IsEffectivelyEmpty(text));

    [Fact]
    public void IsEffectivelyEmpty_OverAnAccessor_AgreesWithTheStringForm_AndStopsEarly()
    {
        // The accessor form runs on the context menu's hot path: a five-thousand-line selection
        // must be answered by reading one character.
        const string text = "   x                                        ";
        var reads = 0;
        var result = SelectionGeometry.IsEffectivelyEmpty(text.Length, i => { reads++; return text[i]; });

        Assert.False(result);
        Assert.Equal(SelectionGeometry.IsEffectivelyEmpty(text), result);
        Assert.Equal(4, reads);
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>One difference between the baseline and the document, as the two places it is: in the baseline's buffer
/// (<see cref="Left"/>) and in the document's (<see cref="Right"/>). Either can be empty: a hunk that only adds lines has
/// an empty left span, one that only removes them an empty right span.</summary>
internal readonly struct Hunk
{
    public Hunk(int index, SnapshotSpan left, SnapshotSpan right)
    {
        Index = index;
        Left = left;
        Right = right;
    }

    /// <summary>Its place in the difference, in the order the hunks come.</summary>
    public int Index { get; }

    public SnapshotSpan Left { get; }
    public SnapshotSpan Right { get; }
}

/// <summary>Finding hunks in a difference view, whichever of the three views it is (inline, the baseline on the left, the
/// document on the right). Editor types only: this is the part of the review that knows how the differencing service
/// numbers things, and nothing else needs to.
/// <para>The lookups follow what Copilot's own hunk commands do for the same views, because the service's answer is not
/// always where the caret is: in the inline view a deleted line is drawn from the baseline's buffer, so a caret on it is
/// in no hunk of the document's, and the hunk has to be found by mapping the baseline's span up into the view.</para></summary>
internal static class HunkLocator
{
    public static bool TryGetModel(ITextView view, out IDifferenceTextViewModel model)
    {
        model = view?.TextViewModel as IDifferenceTextViewModel;
        return model is not null;
    }

    /// <summary>What the viewer shows now, or null before it has computed anything.</summary>
    public static ISnapshotDifference Current(IDifferenceTextViewModel model)
        => model?.Viewer?.DifferenceBuffer?.CurrentSnapshotDifference;

    /// <summary>Every hunk, in order.</summary>
    public static IReadOnlyList<Hunk> All(ISnapshotDifference difference)
    {
        var hunks = new List<Hunk>();
        if (difference is null) { return hunks; }
        var lines = difference.LineDifferences;
        var differences = lines.Differences;
        for (var i = 0; i < differences.Count; i++)
        {
            hunks.Add(ToHunk(difference, differences[i], i));
        }
        return hunks;
    }

    /// <summary>The hunk the caret of <paramref name="view"/> is in or touches.</summary>
    public static bool TryGetHunkAtCaret(ITextView view, out Hunk hunk)
    {
        hunk = default;
        if (!TryGetModel(view, out var model)) { return false; }
        var difference = Current(model);
        if (difference is null) { return false; }

        var caret = view.Caret.Position.BufferPosition;
        Difference found;
        if (model.ViewType == DifferenceViewType.LeftView)
        {
            found = FromLeftPosition(difference, caret.Position);
        }
        else
        {
            difference.FindMatchOrDifference(caret, out _, out found);
            if (found is null && model.ViewType == DifferenceViewType.InlineView)
            {
                found = FromInlinePosition(view, difference, caret.Position);
            }
        }
        if (found is null) { return false; }

        var index = difference.LineDifferences.Differences.IndexOf(found);
        hunk = ToHunk(difference, found, index);
        return true;
    }

    /// <summary>Where the hunk starts in the text of <paramref name="view"/>, for moving the caret there and for
    /// putting something beside it. Null when it has no place in this view.</summary>
    public static SnapshotPoint? AnchorIn(ITextView view, IDifferenceTextViewModel model, ISnapshotDifference difference, Hunk hunk)
    {
        SnapshotPoint point;
        switch (model.ViewType)
        {
            case DifferenceViewType.LeftView:
                point = hunk.Left.Start;
                break;
            case DifferenceViewType.RightView:
                point = hunk.Right.Start;
                break;
            default:
                // Inline: the baseline's lines are drawn among the document's, so a hunk starts where its deleted lines
                // do, or where its added ones do when it deleted none.
                point = difference.MapToInlineSnapshot(hunk.Left.Length != 0 ? hunk.Left.Start : hunk.Right.Start);
                break;
        }
        return point.Snapshot is null ? null : point.TranslateTo(view.TextSnapshot, PointTrackingMode.Negative);
    }

    private static Hunk ToHunk(ISnapshotDifference difference, Difference d, int index)
    {
        var lines = difference.LineDifferences;
        return new Hunk(index,
                        Span(difference.LeftBufferSnapshot, lines.LeftDecomposition, d.Left),
                        Span(difference.RightBufferSnapshot, lines.RightDecomposition, d.Right));
    }

    /// <summary>A difference counts lines; this turns its range of lines into the span of text they are. An empty buffer,
    /// or a range past the end of it, has no span to give: it is an empty one at the start, which is where a hunk against
    /// nothing begins.</summary>
    private static SnapshotSpan Span(ITextSnapshot snapshot, ITokenizedStringList decomposition, Span lines)
    {
        if (snapshot.Length == 0 || lines.Start < 0 || lines.End > ((ICollection<string>)decomposition).Count)
        {
            return new SnapshotSpan(snapshot, 0, 0);
        }
        return new SnapshotSpan(snapshot, decomposition.GetSpanInOriginal(lines));
    }

    private static Difference FromLeftPosition(ISnapshotDifference difference, int position)
    {
        foreach (var hunk in All(difference))
        {
            if (position >= hunk.Left.Start.Position && position <= hunk.Left.End.Position)
            {
                return difference.LineDifferences.Differences[hunk.Index];
            }
        }
        return null;
    }

    private static Difference FromInlinePosition(ITextView view, ISnapshotDifference difference, int position)
    {
        foreach (var hunk in All(difference))
        {
            foreach (var mapped in view.BufferGraph.MapUpToSnapshot(hunk.Left, SpanTrackingMode.EdgeInclusive, view.TextSnapshot))
            {
                if (position >= mapped.Start.Position && position <= mapped.End.Position)
                {
                    return difference.LineDifferences.Differences[hunk.Index];
                }
            }
        }
        return null;
    }
}

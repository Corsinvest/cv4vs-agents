/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Offset-to-line/column arithmetic for a selection, kept free of VS types so it can be
/// tested. Lines are 1-based (what the rest of the code and DTE used); columns are 0-based
/// character offsets within the line — not display columns, so a tab counts as one.</summary>
internal static class SelectionGeometry
{
    /// <summary>A selection worth reporting, or not. Whitespace at the edges is not a selection,
    /// and one character left after trimming is an accidental micro-drag.</summary>
    internal static bool IsEffectivelyEmpty(string text)
        => string.IsNullOrEmpty(text) || text.Trim().Length <= 1;

    /// <param name="lineStartOffsets">Start offset of each line, ascending.</param>
    /// <param name="lineEndOffsets">End offset of each line, excluding its line break.</param>
    internal static (int StartLine, int StartCol, int EndLine, int EndCol) Compute(
        int startOffset, int endOffset, int[] lineStartOffsets, int[] lineEndOffsets)
    {
        var startIdx = LineIndexOf(lineStartOffsets, startOffset);
        var endIdx = LineIndexOf(lineStartOffsets, endOffset);

        // Dragging to the START of a line leaves the end offset on a line the selection holds no
        // character of; reporting it would hand the model one line more than was selected.
        // endIdx > startIdx is what leaves a bare caret alone — it cannot span lines. Deliberately
        // not gated on isEmpty: that says "not worth reporting" (whitespace, one character), and a
        // whitespace-only drag across lines still must not name a line it holds nothing of.
        if (endIdx > startIdx && endOffset == lineStartOffsets[endIdx]) { endIdx--; }

        var startCol = Math.Max(0, startOffset - lineStartOffsets[startIdx]);
        // Clamped to the line's end: after a step-back the original offset sits past it.
        var endCol = Math.Max(0, Math.Min(endOffset, lineEndOffsets[endIdx]) - lineStartOffsets[endIdx]);

        return (startIdx + 1, startCol, endIdx + 1, endCol);
    }

    private static int LineIndexOf(int[] lineStartOffsets, int offset)
    {
        var idx = Array.BinarySearch(lineStartOffsets, offset);
        // BinarySearch returns the complement of the next-larger index when there is no exact hit;
        // the line containing the offset is the one before it.
        return idx >= 0 ? idx : Math.Max(0, ~idx - 1);
    }
}

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
    /// <summary>A selection worth reporting, or not: whitespace alone is not, any real text is —
    /// including a single character, since double-clicking <c>i</c> or <c>T</c> is a real gesture
    /// and there is no caret-only context to fall back to.
    /// <para>Takes an accessor rather than the text so the editor's snapshot can be read in place:
    /// a caller that only wants the answer must not copy a large selection to get it.</para></summary>
    internal static bool IsEffectivelyEmpty(int length, Func<int, char> charAt)
    {
        for (var i = 0; i < length; i++)
        {
            if (!char.IsWhiteSpace(charAt(i))) { return false; }
        }
        return true;
    }

    /// <summary>The same rule over a string already in hand.</summary>
    internal static bool IsEffectivelyEmpty(string text)
        => string.IsNullOrEmpty(text) || IsEffectivelyEmpty(text.Length, i => text[i]);

    /// <param name="lineStartOffsets">Start offset of each line, ascending.</param>
    /// <param name="lineEndOffsets">End offset of each line, excluding its line break.</param>
    internal static (int StartLine, int StartCol, int EndLine, int EndCol) Compute(
        int startOffset, int endOffset, int[] lineStartOffsets, int[] lineEndOffsets)
    {
        var startIdx = LineIndexOf(lineStartOffsets, startOffset);
        var endIdx = LineIndexOf(lineStartOffsets, endOffset);

        // Dragging to the START of a line leaves the end offset on a line the selection holds no
        // character of; reporting it would hand the model one line more than was selected. Keyed on
        // the span, never on what the text turned out to be — blank lines overshoot like any other.
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

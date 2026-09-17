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
    /// <summary>A selection worth reporting, or not: whitespace alone is not, any real text is.
    /// <para>A single character counts. Copilot's own tracker drops one too, but it has somewhere
    /// to fall back to — it degrades the selection to a caret and expands to the surrounding block.
    /// We have no such fallback and suppress caret-only context deliberately, so dropping a
    /// one-character selection would just lose it: double-clicking <c>i</c> or <c>T</c> is a real
    /// gesture, the editor highlights it, and the menu going grey explains nothing.</para></summary>
    /// <param name="length">How many characters there are.</param>
    /// <param name="charAt">The character at an index — a delegate so the editor's snapshot can be
    /// read in place: the context menu asks this on every status query VS raises, and a large
    /// selection must not be copied into a string to answer it.</param>
    internal static bool IsEffectivelyEmpty(int length, Func<int, char> charAt)
    {
        for (var i = 0; i < length; i++)
        {
            if (!char.IsWhiteSpace(charAt(i))) { return false; }
        }
        return true;
    }

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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Lines added and removed between two texts.</summary>
internal readonly struct LineCounts
{
    public LineCounts(int added, int deleted, bool approximate)
    {
        Added = added;
        Deleted = deleted;
        Approximate = approximate;
    }

    public int Added { get; }
    public int Deleted { get; }

    /// <summary>A bound rather than the exact count: the two texts were too large or too different
    /// for the shortest-edit search.</summary>
    public bool Approximate { get; }
}

/// <summary>A pure line-level diff, for the +/- numbers next to a file. Not the editor's differencing
/// services (<c>IDifferenceService</c>, <c>ITextDifferencingService</c>): those are MEF parts, which a unit
/// test cannot compose, and these counts are computed for files that are not open.
/// <para>A changed line counts once on each side (one removed, one added), as a unified diff does.
/// Line endings are not content: <c>\r\n</c> and <c>\n</c> compare equal, so a CRLF file read against
/// an LF backup is not "every line changed".</para></summary>
internal static class LineDiff
{
    // Past this many lines on a side the exact search is skipped outright: it is O((N+M)·D).
    private const int MaxExactLines = 100_000;

    // An edit script longer than this is abandoned for the approximation. Claude's edits are tens of
    // lines; this only trips on a file that was rewritten wholesale.
    private const int MaxEdits = 4_000;

    private const int Unreachable = int.MinValue / 2;

    public static LineCounts Count(string before, string after)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = Encode(Lines(before), ids);
        var b = Encode(Lines(after), ids);

        // What the two share at either end is unchanged by definition and usually most of the file.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) { start++; }
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        var n = endA - start;
        var m = endB - start;
        if (n == 0) { return new LineCounts(m, 0, false); }
        if (m == 0) { return new LineCounts(0, n, false); }
        if (n > MaxExactLines || m > MaxExactLines) { return Approximate(a, start, endA, b, start, endB); }

        var edits = ShortestEdit(a, start, n, b, start, m);
        if (edits < 0) { return Approximate(a, start, endA, b, start, endB); }

        // edits = n + m - 2·lcs
        var lcs = (n + m - edits) / 2;
        return new LineCounts(m - lcs, n - lcs, false);
    }

    /// <summary>Myers' O(ND) search for the length of the shortest edit script, or -1 when it exceeds
    /// <see cref="MaxEdits"/>. States that would leave the edit grid are marked unreachable rather
    /// than followed: a path overshooting the end of one side would otherwise be taken for arriving.</summary>
    private static int ShortestEdit(int[] a, int aStart, int n, int[] b, int bStart, int m)
    {
        var max = Math.Min(n + m, MaxEdits);
        var v = new int[2 * max + 3];
        for (var i = 0; i < v.Length; i++) { v[i] = Unreachable; }
        var offset = max + 1;
        v[offset + 1] = 0;

        for (var d = 0; d <= max; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                var left = v[offset + k - 1];
                var right = v[offset + k + 1];
                var x = k == -d || (k != d && left < right) ? right : left + 1;
                var y = x - k;
                if (x < 0 || x > n || y < 0 || y > m)
                {
                    v[offset + k] = Unreachable;
                    continue;
                }

                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x == n && y == m) { return d; }
            }
        }
        return -1;
    }

    /// <summary>Lines of one side that have no counterpart on the other, by multiset intersection:
    /// order is ignored, so a moved block reads as unchanged. Linear, and never lower than the truth.</summary>
    private static LineCounts Approximate(int[] a, int aStart, int aEnd, int[] b, int bStart, int bEnd)
    {
        var left = new Dictionary<int, int>();
        for (var i = aStart; i < aEnd; i++)
        {
            left.TryGetValue(a[i], out var count);
            left[a[i]] = count + 1;
        }
        var common = 0;
        for (var j = bStart; j < bEnd; j++)
        {
            if (left.TryGetValue(b[j], out var count) && count > 0)
            {
                left[b[j]] = count - 1;
                common++;
            }
        }
        return new LineCounts(bEnd - bStart - common, aEnd - aStart - common, true);
    }

    private static int[] Encode(string[] lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ids.TryGetValue(lines[i], out var id))
            {
                id = ids.Count;
                ids[lines[i]] = id;
            }
            result[i] = id;
        }
        return result;
    }

    /// <summary>Split on <c>\n</c>, dropping the <c>\r</c> before it. A final line with no newline is
    /// a line; a trailing newline does not add an empty one, so "a\nb" and "a\nb\n" are the same text.</summary>
    internal static string[] Lines(string text)
    {
        if (string.IsNullOrEmpty(text)) { return Array.Empty<string>(); }
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') { continue; }
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text.Substring(start, end - start));
            start = i + 1;
        }
        if (start < text.Length)
        {
            var end = text[text.Length - 1] == '\r' ? text.Length - 1 : text.Length;
            lines.Add(text.Substring(start, Math.Max(0, end - start)));
        }
        return lines.ToArray();
    }
}

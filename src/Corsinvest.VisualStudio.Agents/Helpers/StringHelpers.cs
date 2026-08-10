/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Helpers;

internal static class StringHelpers
{
    public static string Truncate(string s, int max = 500)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>Collapse a block of text onto one line, then truncate it. For anything shown in a
    /// menu, a tab or a list row: a session title falls back to the last prompt, which is a whole
    /// message — newlines and all — and one of those in a MenuItem breaks the row rather than
    /// wrapping. Truncating alone would not do: the cut would land inside the first line and drop
    /// the rest silently.</summary>
    public static string ToSingleLine(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) { return s; }
        var collapsed = string.Join(" ", s.Split(['\r', '\n'], System.StringSplitOptions.RemoveEmptyEntries)
                                          .Select(l => l.Trim())
                                          .Where(l => l.Length > 0));
        return Truncate(collapsed, max);
    }

    public static int NonEmptyLineCount(string text)
        => string.IsNullOrEmpty(text)
            ? 0
            : text.Split('\n').Count(l => l.Length > 0);
}

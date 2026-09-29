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

    /// <summary>Quote an argument for CreateProcess (CommandLineToArgvW rules): wrap in quotes if it
    /// has whitespace/quotes, escaping backslashes-before-quote and inner quotes. .NET Framework 4.8
    /// has no ProcessStartInfo.ArgumentList.</summary>
    public static string QuoteProcessArgument(string arg)
    {
        if (!string.IsNullOrEmpty(arg) && arg.IndexOfAny([' ', '\t', '"']) < 0) { return arg; }
        var sb = new System.Text.StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(arg[i]); }
        }
        return sb.Append('"').ToString();
    }
}

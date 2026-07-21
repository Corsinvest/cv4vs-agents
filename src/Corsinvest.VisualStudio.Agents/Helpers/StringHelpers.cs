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

    public static int NonEmptyLineCount(string text)
        => string.IsNullOrEmpty(text)
            ? 0
            : text.Split('\n').Count(l => l.Length > 0);
}

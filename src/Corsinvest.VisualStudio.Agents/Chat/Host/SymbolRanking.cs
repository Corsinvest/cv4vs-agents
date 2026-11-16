/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Corsinvest.VisualStudio.Agents.Ide;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>Orders NavigateTo hits for the `@` picker's Symbols tab. The search itself returns them
/// by file and line, which suits a tool reading every hit but not a person looking for the one they
/// typed: exact name first, then names that start with the query, then the rest; within each, the
/// active editor's file first.</summary>
internal static class SymbolRanking
{
    public static IdeNavigationService.SymbolHit[] Rank(
        IEnumerable<IdeNavigationService.SymbolHit> hits, string query, string activeFile)
    {
        var q = query?.Trim() ?? "";
        var active = Normalize(activeFile);
        return [.. hits
            .OrderBy(h => Group(h.Name, q))
            .ThenBy(h => active.Length > 0 && Normalize(h.File) == active ? 0 : 1)
            .ThenBy(h => h.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.File ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Line)];
    }

    private static int Group(string name, string query)
        => name is null ? 2
            : string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ? 0
            : name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
            : 2;

    private static string Normalize(string path)
        => (path ?? "").Replace('\\', '/').ToLowerInvariant();
}

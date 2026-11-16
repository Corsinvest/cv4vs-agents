/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Linq;
using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;
using SymbolHit = Corsinvest.VisualStudio.Agents.Ide.IdeNavigationService.SymbolHit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The order the `@` picker's Symbols tab shows NavigateTo hits in: what the user typed
/// first, then the file they are in.</summary>
public class SymbolRankingTests
{
    private static SymbolHit Hit(string name, string file = @"C:\p\A.cs", int line = 1)
        => new() { Name = name, File = file, Line = line };

    private static string[] Names(SymbolHit[] hits) => [.. hits.Select(h => h.Name)];

    [Fact]
    public void Rank_ExactBeforePrefixBeforeOther()
        => Assert.Equal(
            new[] { "Search", "SearchFoo", "GetSearchFoo" },
            Names(SymbolRanking.Rank([Hit("GetSearchFoo"), Hit("SearchFoo"), Hit("Search")], "search", null)));

    [Fact]
    public void Rank_ActiveFileFirstWithinGroup()
    {
        var ranked = SymbolRanking.Rank(
            [Hit("SearchA", @"C:\p\Other.cs"), Hit("SearchB", @"C:\p\Active.cs")],
            "Search",
            "c:/p/active.cs");
        Assert.Equal(new[] { "SearchB", "SearchA" }, Names(ranked));
    }

    [Fact]
    public void Rank_ActiveFileDoesNotBeatABetterGroup()
    {
        var ranked = SymbolRanking.Rank(
            [Hit("GetSearch", @"C:\p\Active.cs"), Hit("Search", @"C:\p\Other.cs")],
            "Search",
            @"C:\p\Active.cs");
        Assert.Equal(new[] { "Search", "GetSearch" }, Names(ranked));
    }

    [Fact]
    public void Rank_TieBreakNameFileLine()
    {
        var ranked = SymbolRanking.Rank(
            [Hit("searchB", @"C:\p\B.cs", 9), Hit("SearchA", @"C:\p\B.cs", 1), Hit("SearchA", @"C:\p\A.cs", 5), Hit("SearchA", @"C:\p\A.cs", 2)],
            "Search",
            null);
        Assert.Equal(
            new[] { "SearchA|A|2", "SearchA|A|5", "SearchA|B|1", "searchB|B|9" },
            ranked.Select(h => $"{h.Name}|{System.IO.Path.GetFileNameWithoutExtension(h.File)}|{h.Line}").ToArray());
    }

    [Fact]
    public void Rank_NullActiveFile_NoThrow()
        => Assert.Single(SymbolRanking.Rank([Hit("Search")], "Search", null));

    [Fact]
    public void Rank_NullNameOrFile_NoThrow()
        => Assert.Equal(2, SymbolRanking.Rank([new SymbolHit(), Hit("Search")], "Search", @"C:\p\A.cs").Length);
}

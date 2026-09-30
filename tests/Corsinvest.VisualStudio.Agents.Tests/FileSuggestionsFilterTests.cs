/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Linq;
using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The `@` picker's rows over a listing: substring on the relative path, directories derived
/// from what matched, tree order, a row cap, and nothing that stops at the 20,000th file.</summary>
public class FileSuggestionsFilterTests
{
    private const string Root = @"C:\proj";
    private static readonly string[] Paths = ["docs/readme.md", "docs/chat/plugins.md", "src/App.cs", "README.md"];

    // Directories by name, files as "dir|name", in the order the picker shows them.
    private static string[] Rows(string query) => [.. FileSuggestions.Filter(Root, Paths, query).Select(i => i.IsDir ? i.Name : i.Dir + "|" + i.Name)];

    [Fact]
    public void Empty_query_lists_everything_in_tree_order()
        => Assert.Equal(
            new[] { "docs/", "docs/chat/", "docs/chat|plugins.md", "docs|readme.md", "|README.md", "src/", "src|App.cs" },
            Rows(""));

    [Fact]
    public void Query_matches_the_whole_relative_path_case_insensitively()
        => Assert.Equal(new[] { "docs/chat/", "docs/chat|plugins.md" }, Rows("CHAT"));

    [Fact]
    public void A_directory_is_offered_only_when_it_matches_on_its_own()
        => Assert.Equal(new[] { "src|App.cs" }, Rows("app"));

    [Theory]
    [InlineData("docs/chat")]
    [InlineData(@"docs\chat")]
    public void Slash_or_backslash_in_the_query_matches_the_path(string query)
        => Assert.Contains("docs/chat|plugins.md", Rows(query));

    [Fact]
    public void Item_fields_carry_full_path_and_parent_dir()
    {
        var item = FileSuggestions.Filter(Root, Paths, "plugins").Single(i => !i.IsDir);
        Assert.Equal("plugins.md", item.Name);
        Assert.Equal(@"C:\proj\docs\chat\plugins.md", item.Path);
        Assert.Equal("docs/chat", item.Dir);
        var dir = FileSuggestions.Filter(Root, Paths, "chat").Single(i => i.IsDir);
        Assert.Equal(@"C:\proj\docs\chat", dir.Path);
        Assert.Equal("", dir.Dir);
    }

    [Fact]
    public void Rows_stop_at_MaxRows()
    {
        var many = Enumerable.Range(0, FileSuggestions.MaxRows + 500).Select(i => $"f{i:0000}.cs").ToArray();
        Assert.Equal(FileSuggestions.MaxRows, FileSuggestions.Filter(Root, many, "").Count);
    }

    [Fact]
    public void A_file_past_20000_is_found()
    {
        var many = Enumerable.Range(0, 30000).Select(i => $"m{i / 1000:00}/File{i:00000}.cs").ToArray();
        Assert.Contains(FileSuggestions.Filter(Root, many, "File29999"), i => i.Name == "File29999.cs");
    }
}

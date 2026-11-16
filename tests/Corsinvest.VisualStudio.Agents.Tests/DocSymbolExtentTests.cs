/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Xunit;
using DocSymbol = Corsinvest.VisualStudio.Agents.Ide.IdeNavigationService.DocSymbol;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Which outline node a NavigateTo hit (name + declaration line) stands for, so the `@`
/// picker can take that node's extent.</summary>
public class DocSymbolExtentTests
{
    private static DocSymbol Node(string name, int line, params DocSymbol[] children)
        => new() { Name = name, Line = line, StartLine = line, EndLine = line + 5, Children = children };

    [Fact]
    public void Find_ExactName_ReturnsNode()
    {
        var bar = new DocSymbol { Name = "Bar", Line = 10, StartLine = 8, EndLine = 20 };
        var roots = new[] { Node("Foo", 3, bar) };
        Assert.Same(bar, DocSymbolExtent.Find(roots, 10, "Bar"));
    }

    [Fact]
    public void Find_Overloads_PicksByLine()
    {
        var roots = new[] { Node("Foo", 3, Node("Bar(int)", 10), Node("Bar(string)", 30)) };
        Assert.Equal(30, DocSymbolExtent.Find(roots, 30, "Bar").Line);
    }

    [Fact]
    public void Find_SignatureText_Matches()
    {
        var roots = new[] { Node("Foo", 3, Node("Bar(int)", 10)) };
        Assert.Equal("Bar(int)", DocSymbolExtent.Find(roots, 10, "Bar").Name);
    }

    [Fact]
    public void Find_GenericText_Matches()
    {
        var roots = new[] { Node("List<T>", 4) };
        Assert.Equal("List<T>", DocSymbolExtent.Find(roots, 4, "List").Name);
    }

    [Fact]
    public void Find_NameMismatch_ReturnsNull()
    {
        // Another member on that line is what a buffer with unsaved edits above the symbol looks
        // like: its range would be labelled with the picked name. Better the declaration line alone.
        var roots = new[] { Node("Foo", 3, Node("Baz", 10)) };
        Assert.Null(DocSymbolExtent.Find(roots, 10, "Bar"));
    }

    [Fact]
    public void Find_NamespaceQualifiedTypeText_Matches()
    {
        // C#'s navigation bar names types with their namespace.
        var roots = new[] { Node("Ns.Inner.Foo", 4), Node("Ns.List<T>", 20) };
        Assert.Equal("Ns.Inner.Foo", DocSymbolExtent.Find(roots, 4, "Foo").Name);
        Assert.Equal("Ns.List<T>", DocSymbolExtent.Find(roots, 20, "List").Name);
    }

    [Fact]
    public void Find_TypeAndConstructorOnOneLine_PicksTheType()
    {
        var roots = new[] { Node("Ns.Foo", 7, Node("Foo()", 7)) };
        Assert.Equal("Ns.Foo", DocSymbolExtent.Find(roots, 7, "Foo").Name);
    }

    [Fact]
    public void Find_SuffixThatIsNotAQualifier_DoesNotMatch()
    {
        // "MyFoo" ends with "Foo" but is not "<qualifier>.Foo".
        var roots = new[] { Node("Ns.MyFoo", 7) };
        Assert.Null(DocSymbolExtent.Find(roots, 7, "Foo"));
    }

    [Theory]
    [InlineData("Ns.Foo", "Bar", "Foo.Bar")]
    [InlineData("Ns.List<T>", "Add", "List.Add")]
    [InlineData("Foo", "Bar", "Foo.Bar")]
    [InlineData(null, "Foo", "Foo")]
    [InlineData("", "Foo", "Foo")]
    public void Label_ParentShortNameDotName(string parentText, string name, string expected)
        => Assert.Equal(expected, DocSymbolExtent.Label(parentText, name));

    [Fact]
    public void FindWithParent_ReturnsTheEnclosingNode()
    {
        var bar = Node("Bar(int)", 10);
        var foo = Node("Ns.Foo", 3, bar);
        var (node, parent) = DocSymbolExtent.FindWithParent([Node("Ns", 1, foo)], 10, "Bar");
        Assert.Same(bar, node);
        Assert.Same(foo, parent);
    }

    [Fact]
    public void Find_MatchingNameWinsOverOtherNodeOnSameLine()
    {
        // A one-line class and its member can share a line: the name decides.
        var roots = new[] { Node("Foo", 10, Node("Bar", 10)) };
        Assert.Equal("Bar", DocSymbolExtent.Find(roots, 10, "Bar").Name);
    }

    [Fact]
    public void Find_PrefixThatIsNotASignature_DoesNotMatch()
    {
        // "BarBaz" starts with "Bar" but is another name.
        var roots = new[] { Node("Foo", 3, Node("BarBaz", 10), Node("Bar", 10)) };
        Assert.Equal("Bar", DocSymbolExtent.Find(roots, 10, "Bar").Name);
    }

    [Fact]
    public void Find_NoMatch_ReturnsNull()
    {
        var roots = new[] { Node("Foo", 3, Node("Bar", 10)) };
        Assert.Null(DocSymbolExtent.Find(roots, 99, "Bar"));
    }

    [Fact]
    public void Find_Nested_SearchesDepthFirst()
    {
        var roots = new[] { Node("Ns", 1, Node("Outer", 5, Node("Inner", 20, Node("Run", 40)))) };
        Assert.Equal("Run", DocSymbolExtent.Find(roots, 40, "Run").Name);
    }

    [Fact]
    public void Find_NullRoots_ReturnsNull()
        => Assert.Null(DocSymbolExtent.Find(null, 1, "Bar"));
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Finds the outline node a NavigateTo hit stands for. The hit gives a bare name and the
/// declaration line; the navigation bar's text can carry a signature (<c>Bar(int)</c>), type
/// parameters (<c>List&lt;T&gt;</c>) or, for a C# type, its namespace (<c>Ns.Foo</c>), and
/// overloads share the name, so both the name and the line must agree.</summary>
internal static class DocSymbolExtent
{
    public static IdeNavigationService.DocSymbol Find(IdeNavigationService.DocSymbol[] roots, int line, string name)
        => FindWithParent(roots, line, name).Node;

    /// <summary>The node, and the node that encloses it (null at the top level).</summary>
    public static (IdeNavigationService.DocSymbol Node, IdeNavigationService.DocSymbol Parent) FindWithParent(
        IdeNavigationService.DocSymbol[] roots, int line, string name)
        => Search(roots, null, line, name);

    /// <summary>What the picker writes in parentheses: the enclosing node's short name, then the
    /// symbol's (<c>Foo.Bar</c>), or the name alone at the top level. Built from the outline, not
    /// from NavigateTo's container, which is localized prose ("in Foo (project X)").</summary>
    public static string Label(string parentText, string name)
    {
        var parent = ShortName(parentText);
        return string.IsNullOrEmpty(parent) ? name : $"{parent}.{name}";
    }

    private static (IdeNavigationService.DocSymbol, IdeNavigationService.DocSymbol) Search(
        IdeNavigationService.DocSymbol[] nodes, IdeNavigationService.DocSymbol parent, int line, string name)
    {
        if (nodes is null) { return (null, null); }
        foreach (var node in nodes)
        {
            if (node.Line == line && NameMatches(node.Name, name)) { return (node, parent); }
            if (Search(node.Children, node, line, name) is ({ } hit, var hitParent)) { return (hit, hitParent); }
        }
        return (null, null);
    }

    private static bool NameMatches(string text, string name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) { return false; }
        var bare = StripSuffix(text);
        return bare == name || bare.EndsWith("." + name, StringComparison.Ordinal);
    }

    /// <summary>The text up to a signature or type-parameter list: <c>Bar(int)</c> and
    /// <c>List&lt;T&gt;</c> become <c>Bar</c> and <c>List</c>; VB's <c>(Of T)</c> goes with the
    /// parenthesis.</summary>
    private static string StripSuffix(string text)
    {
        var cut = text.IndexOfAny(['(', '<']);
        return cut < 0 ? text : text.Substring(0, cut);
    }

    private static string ShortName(string text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        var bare = StripSuffix(text);
        var dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare.Substring(dot + 1);
    }
}

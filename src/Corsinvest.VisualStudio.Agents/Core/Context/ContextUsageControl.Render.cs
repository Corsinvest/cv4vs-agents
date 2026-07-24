/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Controls;
using Corsinvest.VisualStudio.Agents.Core.Stats;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>The Context panel rendering — the WPF twin of cv-context-dialog's _renderBody. Data-driven
/// (built in code, not XAML data-templates): header + gauge-bar + memory-map + category table (with an
/// expandable Messages breakdown) + the five expandable trees + footer.</summary>
public partial class ContextUsageControl
{
    private static string Tok(long tokens) => StatsFormat.FormatTokens(tokens);

    // A theme separator line (the tool-window border brush), applied as a DynamicResource so it
    // follows a live light/dark switch — like the Statistics separators.
    private static Border Separator(Thickness thickness, Thickness margin = default, Thickness padding = default)
    {
        var b = new Border { BorderThickness = thickness, Margin = margin, Padding = padding };
        b.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
        return b;
    }

    // A collapsible section with the Fluent-style chevron. CvExpander's template already carries the
    // theme foreground, the stretched header (trailing token at the right edge) and the rotating "›",
    // so this is just a typed constructor.
    private static CvExpander MakeExpander(UIElement header, UIElement content)
        => new() { Header = header, Content = content };

    // Percentage of the window a token count fills (matching the TS _pct: "<0.1%" floor).
    private static string Pct(long tokens, int max)
    {
        if (max <= 0) { return "0%"; }
        var p = tokens / (double)max * 100.0;
        return p >= 0.1 ? $"{p:F1}%" : "<0.1%";
    }

    /// <summary>Populate the panel from a fetched context snapshot.</summary>
    private void RenderContext(GetContextUsageResponse d)
    {
        ContextPanel.Children.Clear();
        if (d == null) { ShowUnavailable(); return; }

        ContextPanel.Children.Add(BuildHeader(d));
        ContextPanel.Children.Add(BuildBar(d));
        if ((d.GridRows?.Length ?? 0) > 0)
        {
            var map = new CvMemoryMap { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
            map.SetData(d.GridRows);
            ContextPanel.Children.Add(map);
        }
        ContextPanel.Children.Add(BuildColumnHeader());
        ContextPanel.Children.Add(BuildCategoryTable(d));
        ContextPanel.Children.Add(BuildTrees(d));
        ContextPanel.Children.Add(BuildFooter(d));
    }

    private UIElement BuildHeader(GetContextUsageResponse d)
    {
        var pct = Math.Max(0, Math.Min(100, (int)Math.Round((double)d.Percentage)));
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = d.Model, FontSize = 15, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Tok(d.TotalTokens)} / {Tok(d.MaxTokens)} ({pct}%)",
            Opacity = 0.8,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    // The segmented gauge-bar: one coloured cell per category, width ∝ tokens/maxTokens. Free space
    // (and zero-width slices) are skipped — the track shows through as the "empty" tail.
    private UIElement BuildBar(GetContextUsageResponse d)
    {
        var bar = new Grid
        {
            Height = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Background = CvContextPalette.EmptyCell,
        };
        var max = d.MaxTokens;
        foreach (var c in d.Categories ?? Array.Empty<ContextCategoryDto>())
        {
            if (c.Name == "Free space" || max <= 0 || c.Tokens <= 0) { continue; }
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Tokens, GridUnitType.Star) });
        }
        // A trailing star column for the remaining free space so the coloured slices stay proportional.
        var used = (d.Categories ?? Array.Empty<ContextCategoryDto>())
            .Where(c => c.Name != "Free space" && c.Tokens > 0).Sum(c => (long)c.Tokens);
        var free = Math.Max(0, max - used);
        if (free > 0) { bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(free, GridUnitType.Star) }); }

        var col = 0;
        foreach (var c in d.Categories ?? Array.Empty<ContextCategoryDto>())
        {
            if (c.Name == "Free space" || max <= 0 || c.Tokens <= 0) { continue; }
            var seg = new Border { Background = CvContextPalette.BrushFor(c.Name), ToolTip = $"{c.Name}: {Tok(c.Tokens)}" };
            Grid.SetColumn(seg, col++);
            bar.Children.Add(seg);
        }
        // Rounded corners on the whole bar.
        bar.SetValue(FrameworkElement.MinHeightProperty, 8.0);
        return new Border { CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = bar, Margin = new Thickness(0, 0, 0, 12) };
    }

    private UIElement BuildColumnHeader()
    {
        var g = ThreeCol();
        g.Margin = new Thickness(0, 0, 0, 4);
        void Cell(string text, int c, TextAlignment align)
        {
            var t = new TextBlock { Text = text, Opacity = 0.6, FontSize = 11, TextAlignment = align };
            Grid.SetColumn(t, c);
            g.Children.Add(t);
        }
        Cell("CATEGORY", 0, TextAlignment.Left);
        Cell("TOKENS", 1, TextAlignment.Right);
        Cell("USAGE", 2, TextAlignment.Right);
        var border = Separator(new Thickness(0, 0, 0, 1), padding: new Thickness(0, 0, 0, 4));
        border.Child = g;
        return border;
    }

    private UIElement BuildCategoryTable(GetContextUsageResponse d)
    {
        var host = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        // Categories by tokens desc, Free space last (matches the TS sort).
        var cats = (d.Categories ?? Array.Empty<ContextCategoryDto>())
            .OrderBy(c => c.Name == "Free space" ? 1 : 0)
            .ThenByDescending(c => c.Tokens)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var c in cats)
        {
            if (c.Name == "Messages" && d.MessageBreakdown != null)
            {
                host.Children.Add(MakeExpander(CategoryRow(c, d.MaxTokens), BuildMessagesBreakdown(d.MessageBreakdown)));
            }
            else
            {
                host.Children.Add(CategoryRow(c, d.MaxTokens));
            }
        }
        return host;
    }

    // A category row: colour dot + name + tokens + share%.
    private UIElement CategoryRow(ContextCategoryDto c, int max)
    {
        var g = ThreeCol();
        g.Margin = new Thickness(0, 3, 0, 3);

        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new Rectangle
        {
            Width = 10, Height = 10, RadiusX = 2, RadiusY = 2,
            Fill = CvContextPalette.BrushFor(c.Name),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        name.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(name, 0);
        g.Children.Add(name);

        var tok = new TextBlock { Text = Tok(c.Tokens), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tok, 1);
        g.Children.Add(tok);

        var pct = new TextBlock { Text = Pct(c.Tokens, max), Opacity = 0.6, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pct, 2);
        g.Children.Add(pct);
        return g;
    }

    private UIElement BuildMessagesBreakdown(ContextMessageBreakdownDto mb)
    {
        var host = new StackPanel { Margin = new Thickness(18, 2, 0, 6) };
        void Row(string label, int tokens)
        {
            if (tokens <= 0) { return; }
            host.Children.Add(SubRow(label, tokens));
        }
        Row("Tool calls", mb.ToolCallTokens);
        Row("Tool results", mb.ToolResultTokens);
        Row("Attachments", mb.AttachmentTokens);
        Row("Assistant", mb.AssistantMessageTokens);
        Row("User", mb.UserMessageTokens);
        Row("Redirected", mb.RedirectedContextTokens);
        Row("Unattributed", mb.UnattributedTokens);
        AddGroup(host, "By tool type", mb.ToolCallsByType);
        AddGroup(host, "By attachment type", mb.AttachmentsByType);
        return host;
    }

    private void AddGroup(Panel host, string title, ContextTokenGroupDto[] items)
    {
        if (items == null || items.Length == 0) { return; }
        var total = items.Sum(g => (long)g.Tokens);
        var body = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        foreach (var g in items.OrderByDescending(x => x.Tokens).ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            body.Children.Add(SubRow(g.Name, g.Tokens));
        }
        host.Children.Add(MakeExpander(GroupHeader(title, items.Length, total), body));
    }

    // The five expandable trees, each rendered only when non-empty.
    private UIElement BuildTrees(GetContextUsageResponse d)
    {
        var host = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
        };
        host.Children.Add(Separator(new Thickness(0, 1, 0, 0), margin: new Thickness(0, 0, 0, 6)));

        if ((d.MemoryFiles?.Length ?? 0) > 0)
        {
            host.Children.Add(Tree("Memory files", d.MemoryFiles.Length,
                d.MemoryFiles.Sum(f => (long)f.Tokens), "/memory",
                Grouped(d.MemoryFiles, f => f.Type, f => f.Tokens, f => SubRowFile(f.Path, f.Tokens))));
        }
        if ((d.Agents?.Length ?? 0) > 0)
        {
            host.Children.Add(Tree("Custom agents", d.Agents.Length,
                d.Agents.Sum(a => (long)a.Tokens), "/agents",
                Grouped(d.Agents, a => a.Source, a => a.Tokens, a => SubRow(a.AgentType, a.Tokens))));
        }
        if (d.Skills != null && d.Skills.TotalSkills > 0)
        {
            host.Children.Add(Tree("Skills", d.Skills.TotalSkills, d.Skills.Tokens, null,
                Grouped(d.Skills.SkillFrontmatter ?? Array.Empty<ContextSkillDto>(),
                    s => s.Source, s => s.Tokens, s => SubRow(s.Name, s.Tokens))));
        }
        if ((d.McpTools?.Length ?? 0) > 0)
        {
            host.Children.Add(Tree("MCP tools", d.McpTools.Length,
                d.McpTools.Sum(t => (long)t.Tokens), null,
                Grouped(d.McpTools, t => t.ServerName, t => t.Tokens, t => SubRow(t.Name, t.Tokens))));
        }
        if (d.SlashCommands != null && d.SlashCommands.TotalCommands > 0)
        {
            // No per-command detail from the CLI — a flat count/tokens row.
            host.Children.Add(GroupHeader($"Slash commands", d.SlashCommands.TotalCommands, d.SlashCommands.Tokens));
        }
        return host;
    }

    // Group a tree's items by key: with ≥2 keys, a collapsible sub-group per key; with one key, a flat
    // list (no pointless chevron). Sorted by tokens desc, then name — like the TS _groupedBody.
    private UIElement Grouped<T>(IEnumerable<T> items, Func<T, string> keyOf, Func<T, int> tokensOf, Func<T, UIElement> row)
    {
        var list = items.ToList();
        var byKey = list.GroupBy(keyOf).ToList();
        var body = new StackPanel();
        if (byKey.Count <= 1)
        {
            foreach (var it in list.OrderByDescending(tokensOf).ThenBy(x => keyOf(x), StringComparer.Ordinal))
            {
                body.Children.Add(row(it));
            }
            return body;
        }
        var groups = byKey
            .Select(g => (name: g.Key, items: g.OrderByDescending(tokensOf).ThenBy(x => keyOf(x), StringComparer.Ordinal).ToList(), tokens: g.Sum(x => (long)tokensOf(x))))
            .OrderByDescending(g => g.tokens).ThenBy(g => g.name, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var sub = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
            foreach (var it in g.items) { sub.Children.Add(row(it)); }
            body.Children.Add(MakeExpander(GroupHeader(g.name, g.items.Count, g.tokens), sub));
        }
        return body;
    }

    private UIElement Tree(string title, int count, long tokens, string slashHint, UIElement body)
        => MakeExpander(HeaderRow(title, count, slashHint, tokens), body);

    // A section/group header row: name + dimmed (count) + optional /slash hint on the left, the token
    // total right-aligned in its own column so every expander's value lines up at the right edge.
    private UIElement HeaderRow(string title, int count, string slashHint, long tokens)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title });
        left.Children.Add(new TextBlock { Text = $" ({count})", Opacity = 0.6 });
        if (slashHint != null) { left.Children.Add(new TextBlock { Text = $"  {slashHint}", Opacity = 0.5 }); }
        Grid.SetColumn(left, 0);
        g.Children.Add(left);

        var tok = new TextBlock { Text = Tok(tokens), Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tok, 1);
        g.Children.Add(tok);
        return g;
    }

    private UIElement GroupHeader(string title, int count, long tokens)
        => HeaderRow(title, count, null, tokens);

    // An indented sub-row: label (wraps) + tokens, right-aligned.
    private UIElement SubRow(string label, int tokens)
        => SubRowCore(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 }, tokens);

    // A sub-row whose name is a clickable link opening the file in the VS editor (memory files). The
    // path is shown relative to the session's working directory (full path in the tooltip), like the TS.
    private UIElement SubRowFile(string path, int tokens)
    {
        var link = new Hyperlink(new Run(RelPath(path, _currentCwd))) { ToolTip = path };
        link.Click += (_, _) => OpenInEditor(path);
        var name = new TextBlock(link) { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        return SubRowCore(name, tokens);
    }

    // Path relative to the working directory (Windows separators), or the full path when it's not
    // under it — ports the WebView's relPath/displayPath.
    private static string RelPath(string path, string cwd)
    {
        if (string.IsNullOrEmpty(path)) { return ""; }
        static string Norm(string p) => (p ?? "").Replace('\\', '/').TrimEnd('/');
        var root = Norm(cwd);
        var full = Norm(path);
        var shown = full;
        if (!string.IsNullOrEmpty(root))
        {
            var prefix = root + "/";
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { shown = full.Substring(prefix.Length); }
        }
        return shown.Replace('/', '\\');
    }

    private UIElement SubRowCore(TextBlock name, int tokens)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 0);
        g.Children.Add(name);
        var tok = new TextBlock { Text = Tok(tokens), Margin = new Thickness(10, 0, 0, 0), Opacity = 0.85, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(tok, 1);
        g.Children.Add(tok);
        return g;
    }

    // Open an absolute file path in the VS editor (memory files come with full paths). Called from a
    // WPF click handler, so we're already on the UI thread.
    private static void OpenInEditor(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path,
                Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame);
            frame?.Show();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("ContextUsageControl.OpenInEditor", ex);
        }
    }

    private UIElement BuildFooter(GetContextUsageResponse d)
    {
        var text = $"Auto-compact: {(d.IsAutoCompactEnabled ? "on" : "off")}";
        if (d.IsAutoCompactEnabled) { text += $" · threshold {Tok(d.AutoCompactThreshold)}"; }
        if (!string.IsNullOrEmpty(d.AutocompactSource)) { text += $" · {d.AutocompactSource}"; }
        var border = Separator(new Thickness(0, 1, 0, 0), margin: new Thickness(0, 12, 0, 0), padding: new Thickness(0, 8, 0, 0));
        border.Child = new TextBlock { Text = text, Opacity = 0.6 };
        return border;
    }

    // A 3-column grid (name * | tokens auto | usage auto) shared by the header and category rows.
    private static Grid ThreeCol()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        return g;
    }
}

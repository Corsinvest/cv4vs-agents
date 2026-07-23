/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.PlatformUI;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Vertical stacked bar chart of per-day tokens (no plotting library — plain WPF shapes).
/// Each bar stacks one segment per model, coloured from the shared palette (same as the MODELS
/// list). A Y axis with "nice" ticks + gridlines and ~8 X date labels frame it, like the WebView
/// chart. Layout: [ Y-ticks | plot(gridlines + bars) ] over a row of date labels.</summary>
internal sealed class CvBarChart : Grid
{
    private const double PlotHeight = 200;
    private const int Ticks = 4; // → 5 gridlines counting 0

    // Hover highlight: a faint tint + an outline in the theme's text colour, so it contrasts on
    // both light and dark backgrounds. Wired via MouseEnter/Leave (not a Style trigger, which
    // proved unreliable on these code-built overlays).
    private static void AttachHover(Border overlay)
    {
        overlay.MouseEnter += (s, e) =>
        {
            var c = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            overlay.Background = Freeze(Color.FromArgb(40, c.R, c.G, c.B));
            overlay.BorderBrush = Freeze(Color.FromArgb(0xD0, c.R, c.G, c.B));
        };
        overlay.MouseLeave += (s, e) =>
        {
            overlay.Background = Brushes.Transparent;
            overlay.BorderBrush = Brushes.Transparent;
        };
    }

    public void SetData(List<DayBar> bars)
    {
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        if (bars == null || bars.Count == 0) { return; }

        var max = bars.Max(b => b.Total);
        var niceMax = NiceCeil(Math.Max(1, max));

        // [ Y-ticks | plot ]  over  [ (spacer) | X labels ].
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(PlotHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Y axis: tick labels top→bottom (niceMax on top, 0 at the bottom).
        var yAxis = new StackPanel { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Stretch };
        for (var i = Ticks; i >= 0; i--)
        {
            yAxis.Children.Add(new TextBlock
            {
                Text = StatsFormat.FormatTokens(niceMax / (double)Ticks * i),
                FontSize = 10,
                Opacity = 0.6,
                Height = PlotHeight / Ticks,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        SetRowCol(yAxis, 0, 0);
        Children.Add(yAxis);

        // Plot: gridlines behind, bars in front.
        var plot = new Grid();
        var grid = new StackPanel();
        for (var i = 0; i <= Ticks; i++)
        {
            grid.Children.Add(new Border
            {
                Height = PlotHeight / Ticks,
                BorderThickness = new Thickness(0, 0, 0, i == Ticks ? 0 : 1),
                BorderBrush = Freeze(Color.FromArgb(28, 0x88, 0x88, 0x88)),
            });
        }
        plot.Children.Add(grid);

        var barsRow = new UniformGrid { Rows = 1, Columns = bars.Count, VerticalAlignment = VerticalAlignment.Bottom, Height = PlotHeight };
        foreach (var b in bars)
        {
            // Each column is a Grid with two layers: the bar stack, and a transparent overlay in
            // FRONT of it. The overlay is the hover/tooltip target — being on top, its highlight
            // tint + outline draw over the bar, and it spans the full column height so the whole
            // column (not just a short bar) responds, like a heatmap cell.
            var cell = new Grid { Margin = new Thickness(0.5, 0, 0.5, 0) };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.9 };
            foreach (var seg in b.Segments)
            {
                var h = seg.Tokens / (double)niceMax * PlotHeight;
                if (h < 0.5) { h = 0.5; }
                var c = StatsPalette.ColorAt(seg.ColorIndex);
                stack.Children.Insert(0, new Rectangle { Fill = Freeze(c), Height = h });
            }
            cell.Children.Add(stack);

            var overlay = new Border
            {
                Background = Brushes.Transparent, // whole column is hit-testable, full height
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                ToolTip = StatsTooltip.Build(b.Info),
            };
            AttachHover(overlay);
            cell.Children.Add(overlay);

            barsRow.Children.Add(cell);
        }
        plot.Children.Add(barsRow);
        SetRowCol(plot, 0, 1);
        Children.Add(plot);

        // X axis: ~8 evenly spaced date labels.
        var xAxis = new UniformGrid { Rows = 1, Columns = bars.Count, Margin = new Thickness(0, 2, 0, 0) };
        var step = bars.Count <= 12 ? 1 : bars.Count / 8;
        for (var i = 0; i < bars.Count; i++)
        {
            var show = bars.Count <= 12 || (step > 0 && i % step == 0);
            xAxis.Children.Add(new TextBlock
            {
                Text = show ? ShortDate(bars[i].Date) : "",
                FontSize = 10,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        SetRowCol(xAxis, 1, 1);
        Children.Add(xAxis);
    }

    private static void SetRowCol(UIElement e, int row, int col)
    {
        SetRow(e, row);
        SetColumn(e, col);
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Round up to a clean ceiling (1/2/5 × 10ⁿ) so the ticks read well and bars don't
    /// touch the top.</summary>
    private static long NiceCeil(long v)
    {
        if (v <= 0) { return 1; }
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var norm = v / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return (long)(nice * mag);
    }

    private static string ShortDate(string date)
        => System.DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("d/M", System.Globalization.CultureInfo.CurrentCulture)
            : date ?? "";
}

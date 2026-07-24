/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>GitHub-style activity heatmap: columns of 7 squares (a week each), coloured by a 0–4
/// intensity ramp, with a Less→More legend. Built in code (no XAML) so it can generate the grid
/// from the data. Mirrors the WebView's cv-heatmap; the blue ramp is the same.</summary>
internal sealed class CvHeatmap : StackPanel
{
    private const int CellSize = 11;
    private const int Gap = 3;

    // Intensity ramp: 0 = empty (neutral), 1–4 climbing blue (same hues as cv-heatmap).
    private static readonly Brush[] Ramp =
    {
        Freeze(Color.FromRgb(0x3A, 0x3A, 0x3D)), // 0 — neutral square
        Freeze(Color.FromRgb(0x2B, 0x6C, 0xB0)), // 1
        Freeze(Color.FromRgb(0x3B, 0x82, 0xF6)), // 2
        Freeze(Color.FromRgb(0x60, 0xA5, 0xFA)), // 3
        Freeze(Color.FromRgb(0x93, 0xC5, 0xFD)), // 4
    };

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void SetData(List<HeatCell[]> cols)
    {
        Children.Clear();
        if (cols == null || cols.Count == 0) { return; }

        // Grid of week-columns.
        var grid = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var col in cols)
        {
            var week = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, Gap, 0) };
            foreach (var cell in col)
            {
                week.Children.Add(Square(cell));
            }
            grid.Children.Add(week);
        }
        Children.Add(grid);

        // Legend: Less ▢▢▢▢▢ More.
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        legend.Children.Add(new TextBlock { Text = "Less", Opacity = 0.7, FontSize = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        for (var i = 0; i <= 4; i++) { legend.Children.Add(Swatch(Ramp[i])); }
        legend.Children.Add(new TextBlock { Text = "More", Opacity = 0.7, FontSize = 11, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Children.Add(legend);
    }

    // Cells brighten slightly on hover (a subtle highlight, like the WebView heatmap).
    private static readonly Style HoverStyle = BuildHoverStyle();

    private static Style BuildHoverStyle()
    {
        var s = new Style(typeof(Rectangle));
        var t = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        t.Setters.Add(new Setter(UIElement.OpacityProperty, 0.7));
        s.Triggers.Add(t);
        return s;
    }

    private static Rectangle Square(HeatCell cell)
    {
        var r = new Rectangle
        {
            Width = CellSize,
            Height = CellSize,
            RadiusX = 2,
            RadiusY = 2,
            Margin = new Thickness(0, 0, 0, Gap),
            Fill = cell.Future ? Brushes.Transparent : Ramp[cell.Intensity],
        };
        if (!cell.Future)
        {
            r.ToolTip = StatsTooltip.Build(cell.Info);
            r.Style = HoverStyle;
        }
        return r;
    }

    private static Rectangle Swatch(Brush fill) => new()
    {
        Width = CellSize,
        Height = CellSize,
        RadiusX = 2,
        RadiusY = 2,
        Margin = new Thickness(2, 0, 2, 0),
        Fill = fill,
    };
}

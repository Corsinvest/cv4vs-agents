/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Donut chart of a node's token breakdown by child (no plotting library — plain WPF
/// shapes). One ring segment per slice, coloured from the shared palette (same as the model dots);
/// the "Others" slice is grey. The total sits in the hole. Each segment hovers/tooltips with the
/// shared StatsTooltip, like the heatmap cells and chart bars.</summary>
internal sealed class CvDonut : Grid
{
    private const double Size = 180;      // outer diameter
    private const double Ring = 34;       // ring thickness
    private static readonly Brush OthersBrush = Freeze(Color.FromRgb(0x6B, 0x72, 0x80));

    public void SetData(List<StatsService.DonutSlice> slices)
    {
        Children.Clear();
        var total = slices?.Sum(s => s.Tokens) ?? 0;
        if (slices == null || slices.Count == 0 || total <= 0) { return; }

        // Layout: [ donut ] [ legend ].
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var ringBox = new Grid { Width = Size, Height = Size };
        var canvas = new Canvas { Width = Size, Height = Size };
        double cx = Size / 2, cy = Size / 2, rOuter = Size / 2, rInner = rOuter - Ring;

        var legend = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var start = -90.0; // 12 o'clock
        var idx = 0;
        foreach (var s in slices)
        {
            var sweep = s.Tokens / (double)total * 360.0;
            var end = start + sweep;
            var isOthers = s.Label == "Others";
            var fill = isOthers ? OthersBrush : StatsPalette.BrushAt(idx);

            // A single 100% slice is a full ring: a 360° arc degenerates (start point == end point),
            // so draw a closed ring (outer circle minus inner hole) instead.
            var data = sweep >= 359.99 ? FullRing(cx, cy, rOuter, rInner) : RingSegment(cx, cy, rOuter, rInner, start, end);
            canvas.Children.Add(new Path
            {
                Fill = fill,
                Data = data,
                ToolTip = StatsTooltip.Build(s.Tooltip),
                Style = HoverStyle,
            });
            legend.Children.Add(LegendRow(fill, s.Label, s.Tokens, s.Tokens / (double)total * 100.0));

            start = end;
            if (!isOthers) { idx++; }
        }

        // Total in the hole.
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(new TextBlock
        {
            Text = StatsFormat.FormatTokens(total),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        center.Children.Add(new TextBlock { Text = "tokens", FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });

        ringBox.Children.Add(canvas);
        ringBox.Children.Add(center);
        row.Children.Add(ringBox);
        row.Children.Add(legend);
        Children.Add(row);
    }

    // A legend entry: colour dot + label + share-of-total %.
    // A legend entry: colour dot + label + tokens + share-of-total %.
    private static UIElement LegendRow(Brush fill, string label, long tokens, double pct)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 220 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Rectangle { Width = 9, Height = 9, RadiusX = 2, RadiusY = 2, Fill = fill, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(dot, 0);
        g.Children.Add(dot);

        var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(name, 1);
        g.Children.Add(name);

        var tok = new TextBlock { Text = StatsFormat.FormatTokens(tokens), Opacity = 0.8, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(tok, 2);
        g.Children.Add(tok);

        var share = new TextBlock { Text = $"{pct:F0}%", Opacity = 0.6, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(share, 3);
        g.Children.Add(share);
        return g;
    }

    // A full ring (100%): the outer disc with the inner hole punched out (EvenOdd fill).
    private static Geometry FullRing(double cx, double cy, double rOuter, double rInner)
    {
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new EllipseGeometry(new Point(cx, cy), rOuter, rOuter));
        geo.Children.Add(new EllipseGeometry(new Point(cx, cy), rInner, rInner));
        geo.Freeze();
        return geo;
    }

    // A closed ring wedge between two angles: outer arc forward, inner arc back.
    private static Geometry RingSegment(double cx, double cy, double rOuter, double rInner, double a0, double a1)
    {
        var isLarge = (a1 - a0) >= 180;
        Point P(double r, double a)
        {
            var rad = a * Math.PI / 180.0;
            return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        }

        var fig = new PathFigure { StartPoint = P(rOuter, a0), IsClosed = true };
        fig.Segments.Add(new ArcSegment(P(rOuter, a1), new Size(rOuter, rOuter), 0, isLarge, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(P(rInner, a1), true));
        fig.Segments.Add(new ArcSegment(P(rInner, a0), new Size(rInner, rInner), 0, isLarge, SweepDirection.Counterclockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    // Slightly brighten the hovered slice (a subtle highlight, like the heatmap cells).
    private static readonly Style HoverStyle = BuildHoverStyle();

    private static Style BuildHoverStyle()
    {
        var s = new Style(typeof(Path));
        var t = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        t.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75));
        s.Triggers.Add(t);
        return s;
    }

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

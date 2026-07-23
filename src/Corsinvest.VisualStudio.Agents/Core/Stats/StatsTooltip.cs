/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>The shared hover card for a day — identical whether you hover a heatmap square or a
/// chart bar: full date, the activity line (messages · sessions · tools), then a coloured row per
/// model (dot · name · tokens · share-of-day), largest first.</summary>
internal static class StatsTooltip
{
    public static object Build(DayInfo info)
    {
        var panel = new StackPanel { MinWidth = 180 };

        panel.Children.Add(new TextBlock
        {
            Text = FullDate(info.Date),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        var activity = $"{info.Messages:N0} messages · {info.Sessions:N0} sessions · {info.Tools:N0} tools";
        panel.Children.Add(new TextBlock
        {
            Text = activity,
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, info.Segments.Count > 0 ? 6 : 0),
        });

        foreach (var seg in info.Segments.OrderByDescending(s => s.Tokens))
        {
            var pct = info.Total > 0 ? seg.Tokens / (double)info.Total * 100.0 : 0;
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Rectangle
            {
                Width = 9, Height = 9, RadiusX = 2, RadiusY = 2,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = FrozenBrush(StatsPalette.ColorAt(seg.ColorIndex)),
            };
            Grid.SetColumn(dot, 0);
            row.Children.Add(dot);

            var name = new TextBlock { Text = seg.Model, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var val = new TextBlock
            {
                Text = $"{StatsFormat.FormatTokens(seg.Tokens)}  ({pct:F0}%)",
                Opacity = 0.7,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(val, 2);
            row.Children.Add(val);

            panel.Children.Add(row);
        }

        return panel;
    }

    private static Brush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static string FullDate(string date)
        => System.DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)
            : date ?? "";
}

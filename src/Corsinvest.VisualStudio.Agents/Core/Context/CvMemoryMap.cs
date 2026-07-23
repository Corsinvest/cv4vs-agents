/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Stats;

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>The context memory-map: a grid of small square cells, one per GridRows cell, coloured by
/// its category (empty cells a neutral grey). Mirrors the WebView dialog's grid (_renderGrid). Not a
/// heatmap (that's GitHub-style per-day) — this is a linear fill of the context window.</summary>
internal sealed class CvMemoryMap : Grid
{
    public void SetData(ContextGridCellDto[][] rows)
    {
        Children.Clear();
        if (rows == null || rows.Length == 0) { return; }

        var cols = rows[0]?.Length ?? 0;
        var panel = new UniformGrid { Rows = rows.Length, Columns = cols };
        foreach (var row in rows)
        {
            if (row == null) { continue; }
            foreach (var cell in row)
            {
                var fill = cell != null && cell.IsFilled
                    ? CvContextPalette.BrushFor(cell.CategoryName)
                    : CvContextPalette.EmptyCell;
                var rect = new Rectangle
                {
                    Fill = fill,
                    RadiusX = 1,
                    RadiusY = 1,
                    Margin = new Thickness(1),
                    Stretch = Stretch.Fill,
                };
                if (cell != null && cell.IsFilled)
                {
                    rect.ToolTip = $"{cell.CategoryName} · {StatsFormat.FormatTokens(cell.Tokens)}";
                }
                panel.Children.Add(rect);
            }
        }
        Children.Add(panel);
    }
}

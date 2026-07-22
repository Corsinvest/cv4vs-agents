/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows.Media;
using Corsinvest.VisualStudio.Agents.Contracts;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Row model for the Statistics MODELS list: the DTO's numbers plus the palette colour for
/// its index, so the dot and the share bar match the model's colour in the daily chart.</summary>
internal sealed class ModelRow
{
    public string Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double Percentage { get; set; }
    public Brush Color { get; set; }

    public static ModelRow From(StatsModelDto d, int index) => new()
    {
        Model = d.Model,
        InputTokens = d.InputTokens,
        OutputTokens = d.OutputTokens,
        Percentage = d.Percentage,
        Color = StatsPalette.BrushAt(index),
    };
}

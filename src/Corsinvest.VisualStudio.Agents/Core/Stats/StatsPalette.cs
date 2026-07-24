/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>One stable colour per model, keyed by its index in the (token-sorted) breakdown — the
/// same colour is used for the model's dot, its share bar, and its bars in the daily chart, so a
/// model reads as one colour everywhere. Mirrors the WebView dialog's MODEL_COLORS. The hues stay
/// legible on both the light and dark VS themes.</summary>
internal static class StatsPalette
{
    private static readonly Color[] Colors =
    {
        Color.FromRgb(0x3B, 0x82, 0xF6), // blue
        Color.FromRgb(0x8B, 0x5C, 0xF6), // lavender
        Color.FromRgb(0x14, 0xB8, 0xA6), // teal
        Color.FromRgb(0x22, 0xC5, 0x5E), // green
        Color.FromRgb(0xF5, 0x9E, 0x0B), // amber
        Color.FromRgb(0xEC, 0x48, 0x99), // pink
        Color.FromRgb(0x06, 0xB6, 0xD4), // cyan
        Color.FromRgb(0xEF, 0x44, 0x44), // red
    };

    public static Color ColorAt(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

    public static Brush BrushAt(int index)
    {
        var b = new SolidColorBrush(ColorAt(index));
        b.Freeze();
        return b;
    }
}

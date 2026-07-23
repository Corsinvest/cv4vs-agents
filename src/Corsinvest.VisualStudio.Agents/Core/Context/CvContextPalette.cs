/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>Category-name → colour, ported from the WebView dialog's CATEGORY_BY_NAME
/// (cv-context-dialog.ts). The CLI's per-category Color symbol reuses the same value across
/// categories (promptBorder / inactive), so we key on the stable category NAME instead — like VS
/// Code. Vivid mid-tone hues, legible on both the light and dark VS themes.</summary>
internal static class CvContextPalette
{
    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush Neutral = Freeze(0x8A, 0x8A, 0x8A); // System prompt + fallback
    private static readonly Brush Blue = Freeze(0x3B, 0x82, 0xF6);
    private static readonly Brush Teal = Freeze(0x14, 0xB8, 0xA6);
    private static readonly Brush Green = Freeze(0x22, 0xC5, 0x5E);
    private static readonly Brush Yellow = Freeze(0xEA, 0xB3, 0x08);
    private static readonly Brush DarkOrange = Freeze(0xF9, 0x73, 0x16);
    private static readonly Brush Lavender = Freeze(0x8B, 0x5C, 0xF6);

    /// <summary>The unfilled memory-map cell (never a category colour).</summary>
    public static readonly Brush EmptyCell = Freeze(0x5A, 0x5A, 0x5A);

    // Same keys as the TS CATEGORY_BY_NAME. Free space maps to the empty-cell grey (a track, never a
    // filled colour).
    private static readonly Dictionary<string, Brush> ByName = new()
    {
        ["System prompt"] = Neutral,
        ["System tools"] = Blue,
        ["System tools (deferred)"] = Teal,
        ["Custom agents"] = Green,
        ["Memory files"] = Yellow,
        ["Skills"] = DarkOrange,
        ["Messages"] = Lavender,
        ["Free space"] = EmptyCell,
    };

    /// <summary>Colour for a category by name; neutral grey for anything unmapped (like the TS
    /// fallback).</summary>
    public static Brush BrushFor(string categoryName)
        => categoryName != null && ByName.TryGetValue(categoryName, out var b) ? b : Neutral;
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Utils;

internal static class ColorUtils
{
    /// <summary>
    /// True when a color is "dark" (ITU-R BT.601 luma &lt; 128). Takes raw RGB
    /// bytes so it works for both System.Drawing and System.Windows.Media colors.
    /// </summary>
    public static bool IsDark(byte r, byte g, byte b)
        => ((r * 299) + (g * 587) + (b * 114)) / 1000 < 128;
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 *
 * The 16-entry ANSI palettes below are standard console colors, stored as COLORREF
 * (0x00BBGGRR) — the byte order Windows Terminal's ColorTable expects.
 */

using Microsoft.Terminal.Wpf;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System.Drawing;

namespace Corsinvest.VisualStudio.Agents.Cli;

/// <summary>
/// Produces a <see cref="TerminalTheme"/> for the ConPTY terminal that tracks the active VS
/// theme: a dark or light 16-color ANSI palette plus fg/bg/selection pulled live from VS's
/// themed colors. Values are COLORREF (BGR byte order), the Windows Terminal convention.
/// </summary>
internal static class TerminalThemer
{
    // COLORREF (0x00BBGGRR): standard ANSI slots 0-15 (normal 0-7, bright 8-15).
    private static readonly uint[] DarkPalette =
    [
        0x000000, // black
        0x3131cd, // red
        0x79bc0d, // green
        0x10e5e5, // yellow
        0xc87224, // blue
        0xbc3fbc, // magenta
        0xcda811, // cyan
        0xe5e5e5, // white
        0x666666, // bright black
        0x4c4cf1, // bright red
        0x8bd123, // bright green
        0x43f5f5, // bright yellow
        0xea8e3b, // bright blue
        0xd670d6, // bright magenta
        0xdbb829, // bright cyan
        0xe5e5e5, // bright white
    ];

    private static readonly uint[] LightPalette =
    [
        0x000000, // black
        0x3131cd, // red
        0x008000, // green
        0x007370, // yellow
        0xa55104, // blue
        0xbc05bc, // magenta
        0x977900, // cyan
        0x555555, // white
        0x666666, // bright black
        0x3131cd, // bright red
        0x008000, // bright green
        0x007370, // bright yellow
        0xa55104, // bright blue
        0xbc05bc, // bright magenta
        0x977900, // bright cyan
        0x555555, // bright white
    ];

    /// <summary>Build a terminal theme matching the current VS theme. UI thread only
    /// (reads the VS color service).</summary>
    public static TerminalTheme GetTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var background = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        var foreground = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
        var selection = VSColorTheme.GetThemedColor(CommonControlsColors.ComboBoxTextInputSelectionColorKey);

        return new TerminalTheme
        {
            ColorTable = Utils.ColorUtils.IsDark(background.R, background.G, background.B) ? DarkPalette : LightPalette,
            DefaultBackground = ToColorRef(background),
            DefaultForeground = ToColorRef(foreground),
            DefaultSelectionBackground = ToColorRef(selection),
            CursorStyle = CursorStyle.BlinkingBlockDefault,
        };
    }

    private static uint ToColorRef(Color c) => (uint)ColorTranslator.ToWin32(c);
}

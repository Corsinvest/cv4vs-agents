/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary>
/// Themes the parts of a window WPF can't reach.
/// <para>A dialog's content follows the VS theme through WPF resources, but the title bar and the
/// border are drawn by Windows outside the visual tree — so a dark VS on a light Windows gets a
/// white bar around themed content. DWM is the only way in.</para>
/// </summary>
internal static class WindowChrome
{
    /// <summary>Match a window's title bar and border to the current VS theme. Call once the window
    /// has a handle (SourceInitialized); before that there is nothing to theme.
    /// <para>Each attribute is best-effort: they arrived in different Windows versions, and an
    /// older build simply keeps its default chrome instead of failing.</para></summary>
    public static void ApplyTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) { return; }

        try
        {
            var dark = VsThemeReader.IsDark() ? 1 : 0;
            // 20 since Windows 10 20H1; 19 was the pre-release id, worth trying as a fallback.
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }

            // The border is drawn separately from the caption and stays light otherwise. Read from
            // the theme rather than hardcoding, so it tracks whatever VS is set to.
            var border = ThemedBorderColor();
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception ex) { OutputWindowLogger.LogException($"{nameof(WindowChrome)}.{nameof(ApplyTheme)}", ex); }
    }

    /// <summary>The border colour as a COLORREF (0x00BBGGRR — the reverse of RGB), taken from the
    /// same theme key the tool windows use. Falls back to DWMWA_COLOR_DEFAULT so Windows picks one
    /// if the shell can't be reached.</summary>
    private static int ThemedBorderColor()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell5;
            var c = shell.GetThemedWPFColor(EnvironmentColors.ToolWindowBorderColorKey);
            return c.R | (c.G << 8) | (c.B << 16);
        }
        catch { return DwmwaColorDefault; }
    }

    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    // Windows 11 22000+; older builds fail the call and keep the default border.
    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary>Helpers for pane-attention notifications: whether VS has the OS focus (so we know
/// whether an in-VS InfoBar is enough), and an OS toast when it doesn't — the toast is layout-proof
/// (works with tiling window managers, second monitors, hidden taskbars) unlike a taskbar flash.</summary>
internal static class Win32Focus
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>The VS main window handle, or IntPtr.Zero if unavailable. Read on the UI thread.</summary>
    private static IntPtr MainWindowHandle()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell
            && shell.GetDialogOwnerHwnd(out var hwnd) == Microsoft.VisualStudio.VSConstants.S_OK)
        {
            return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>True when the VS main window is the OS foreground window (the user is inside VS).
    /// When false, the user is in another app / another tile → an in-VS InfoBar alone can be missed,
    /// so we also raise an OS toast.</summary>
    public static bool IsVsForeground()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var main = MainWindowHandle();
        return main != IntPtr.Zero && GetForegroundWindow() == main;
    }

    /// <summary>Raise an OS balloon toast. A short-lived tray NotifyIcon shows the balloon then
    /// disposes itself — no persistent tray icon. Appears above everything regardless of window
    /// layout, so it reaches the user in another app / tile / monitor. Clicking it brings VS to the
    /// front and runs <paramref name="onClick"/> (e.g. activate the owning pane).</summary>
    public static void ShowToast(string title, string message, Action onClick = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var icon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Information,
                Visible = true,
                BalloonTipTitle = title,
                BalloonTipText = message,
            };
            // Dispose the tray icon once the balloon has run its course (or the user closes it).
            void cleanup(object s, EventArgs e) => icon.Dispose();
            icon.BalloonTipClosed += cleanup;
            icon.BalloonTipClicked += (s, e) =>
            {
                // Bring VS to the front, then let the caller go to the right pane.
                var main = MainWindowHandle();
                if (main != IntPtr.Zero) { SetForegroundWindow(main); }
                onClick?.Invoke();
                icon.Dispose();
            };
            icon.ShowBalloonTip(5000);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Win32Focus.ShowToast", ex);
        }
    }
}

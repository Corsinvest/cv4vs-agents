/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Reflection;
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
    /// front and runs <paramref name="onClick"/> (e.g. activate the owning pane).
    /// Returns a handle that takes the balloon down early (the user dealt with the pane before it
    /// expired), or null when the toast could not be shown. Disposing it twice is harmless.
    /// <paramref name="onClosed"/> runs when the balloon goes away on its own, so the caller can
    /// drop the handle it is holding.</summary>
    public static IDisposable ShowToast(string title, string message, Action onClick = null,
                                        Action onClosed = null)
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
            var handle = new ToastHandle(icon);
            // Drop the tray icon once the balloon has run its course (or the user closes it).
            icon.BalloonTipClosed += (s, e) =>
            {
                handle.Dispose();
                onClosed?.Invoke();
            };
            icon.BalloonTipClicked += (s, e) =>
            {
                // Bring VS to the front, then let the caller go to the right pane.
                var main = MainWindowHandle();
                if (main != IntPtr.Zero) { SetForegroundWindow(main); }
                onClick?.Invoke();
                handle.Dispose();
                onClosed?.Invoke();
            };
            icon.ShowBalloonTip(5000);
            return handle;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("Win32Focus.ShowToast", ex);
            return null;
        }
    }

    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_INFO = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA data);

    /// <summary>Takes a balloon that is still on screen down. Neither deleting the icon nor the
    /// Visible off/on round-trip retracts one already showing (both verified on Windows 11 — it
    /// stays until it times out). What does is the documented call: NIM_MODIFY carrying NIF_INFO
    /// with an empty szInfo, which the shell reads as "no balloon for this icon". WinForms keeps
    /// the hWnd/uID that call needs private, hence the reflection — field names are the .NET
    /// Framework ones (`window`, `id`), which differ from .NET Core's.</summary>
    private sealed class ToastHandle(NotifyIcon icon) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) { return; }
            _done = true;
            RetractBalloon();
            icon.Dispose();
        }

        private void RetractBalloon()
        {
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var type = typeof(NotifyIcon);
                var window = type.GetField("window", flags)?.GetValue(icon) as NativeWindow;
                var id = type.GetField("id", flags)?.GetValue(icon);
                if (window == null || window.Handle == IntPtr.Zero || id == null)
                {
                    OutputWindowLogger.Global.Warn("[panes] balloon retract skipped: NotifyIcon internals not found");
                    return;
                }

                var data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                    hWnd = window.Handle,
                    uID = (int)id,
                    uFlags = NIF_INFO,
                    szInfo = string.Empty,
                    szInfoTitle = string.Empty,
                    szTip = string.Empty,
                };
                if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
                {
                    OutputWindowLogger.Global.Warn("[panes] balloon retract refused by the shell");
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.Warn($"[panes] balloon retract failed: {ex.Message}");
            }
        }
    }
}

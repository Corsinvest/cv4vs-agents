/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Corsinvest.VisualStudio.Agents.Chat.Pane;

/// <summary>
/// The chat's WebView2, forwarding the keys composition rendering drops.
/// <para>Rendering through Windows.UI.Composition means input is handed to the browser by hand
/// rather than reaching a child HWND. The forwarding is incomplete: CoreWebView2CompositionController
/// has SendMouseInput and SendPointerInput but no keyboard counterpart, and the control leaves
/// IKeyboardInputSink.TranslateAccelerator unimplemented. The dropped keys fall through to Visual
/// Studio, which acts on them as its own commands — Home/End move the caret to the start/end of a
/// document instead of a line.</para>
/// <para>So claim them here and let the page act — the same shape as Esc, which
/// <see cref="ChatPaneWindow"/> claims from VS and forwards over the bridge.</para>
/// </summary>
internal sealed class ChatWebView : WebView2CompositionControl
{
    /// <summary>Raised for a claimed key, in the shape the page expects.</summary>
    internal event Action<HostKeyNotification> HostKeyPressed;

    // Only keys verified as dropped. A key that already reaches the browser must stay out: claiming
    // it sets Handled and would take it away from the page, breaking what works today (arrows,
    // PageUp/PageDown and Ctrl+Left/Right all arrive fine). Esc and Ctrl+F are out for another
    // reason — VS turns those into commands before any of this, and ChatPaneWindow already claims
    // them through IOleCommandTarget.
    //
    // The value is the DOM KeyboardEvent.key name: the page then matches the same strings a real
    // key event would carry, instead of translating WPF enum names.
    private static readonly Dictionary<Key, string> ClaimedKeys = new()
    {
        [Key.Home] = "Home",
        [Key.End] = "End",
    };

    // PreviewKeyDown, not KeyDown: the key has to be claimed before it tunnels down to whatever
    // WPF would otherwise route it to.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        // Alt+Home/End is browser navigation (back/forward), not ours to take.
        if ((mods & ModifierKeys.Alt) != 0 || !ClaimedKeys.TryGetValue(e.Key, out var domKey))
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        HostKeyPressed?.Invoke(new HostKeyNotification
        {
            Key = domKey,
            Ctrl = (mods & ModifierKeys.Control) != 0,
            Shift = (mods & ModifierKeys.Shift) != 0,
            Alt = false,
        });
        e.Handled = true;
    }

    /// <summary>The browser's own task manager — the Edge one, with live memory/CPU per process.
    /// It covers every pane on the user-data folder, panes in another VS instance included, which
    /// is what makes it worth having: a stray renderer or a process count that doesn't add up is a
    /// question about the whole browser, not about this pane.
    /// <para>Under composition rendering the browser gives that window no usable frame: Windows
    /// reserves the space once WS_CAPTION is set, but Chromium paints over it, so no title bar ever
    /// shows — verified against every combination of SetWindowLong, RedrawWindow, resize and
    /// hide/show. <see cref="HostTaskManagerWindow"/> sidesteps it: the frame belongs to a window
    /// of ours and only the content stays theirs.</para></summary>
    internal void OpenTaskManager()
    {
        var core = CoreWebView2;
        if (core == null) { return; }

        // The browser only ever has one task manager; asking again would just raise its window.
        if (_taskManagerHost != null)
        {
            _taskManagerHost.Activate();
            return;
        }

        // Hook FIRST: the point is to catch the window as it appears. Searching for it afterwards
        // can't work — the chat's own windows live in the same process under the same class and
        // are equally frameless, so nothing tells them apart. Being there when it appears does.
        HookBrowserWindows(core.BrowserProcessId);
        core.OpenTaskManagerWindow();
    }

    private const int GwlStyle = -16;
    private const int WsChild = 0x40000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const uint SwpSizeOnly = 0x0004 | 0x0010; // SWP_NOZORDER | SWP_NOACTIVATE
    // SHOW, not CREATE: at creation the window exists but Chromium has not finished configuring it.
    private const uint EventObjectShow = 0x8002;
    private const int ObjidWindow = 0;
    private const int ChildidSelf = 0;
    private const uint WineventOutOfContext = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback,
                                                 uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hWnd,
                                       int objectId, int childId, uint threadId, uint time);

    // Held for the lifetime of the hook: the callback is handed to unmanaged code, and a collected
    // delegate would leave user32 calling into freed memory.
    private WinEventProc _winEventCallback;
    private IntPtr _winEventHook;
    private DispatcherTimer _unhookTimer;
    // Held because nothing else references this window: a collected one would take the HWND the
    // browser's window is parented to down with it.
    private DialogWindow _taskManagerHost;

    /// <summary>Watch the browser process for a window being shown, briefly. Catching it as it
    /// appears is what makes it identifiable at all: the chat's own windows live in the same
    /// process under the same class and are equally frameless, but they were shown long ago.</summary>
    private void HookBrowserWindows(uint browserPid)
    {
        Unhook();
        _winEventCallback = OnBrowserWindowShown;
        _winEventHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero,
                                        _winEventCallback, browserPid, 0, WineventOutOfContext);
        if (_winEventHook == IntPtr.Zero)
        {
            OutputWindowLogger.Warn("[pane] task manager: window hook not installed");
            return;
        }

        // The window appears within a few hundred ms. Keep the window short: a chat opened while
        // this is armed shows a window of its own, and adopting that one would empty its pane.
        _unhookTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _unhookTimer.Tick += (s, e) => Unhook();
        _unhookTimer.Start();
    }

    private void OnBrowserWindowShown(IntPtr hook, uint eventType, IntPtr hWnd,
                                      int objectId, int childId, uint threadId, uint time)
    {
        // Chromium creates plenty of objects; only a top-level window of its own is a candidate.
        if (hWnd == IntPtr.Zero || objectId != ObjidWindow || childId != ChildidSelf) { return; }
        if ((GetWindowLong(hWnd, GwlStyle) & WsChild) != 0) { return; }

        Unhook();
        HostTaskManagerWindow(hWnd);
    }

    /// <summary>Put the browser's window inside one of ours, so it gets a real title bar.</summary>
    private void HostTaskManagerWindow(IntPtr child)
    {
        var host = new DialogWindow
        {
            Title = "WebView task manager",
            Width = 1000,
            Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        host.SourceInitialized += (s, e) =>
        {
            Helpers.WindowChrome.ApplyTheme(host);
            var hostHandle = new WindowInteropHelper(host).Handle;
            // A child window can't be a popup; swap the styles before reparenting or the window
            // keeps trying to lay itself out as a top-level one.
            var style = GetWindowLong(child, GwlStyle);
            SetWindowLong(child, GwlStyle, (style | WsChild) & ~WsPopup);
            SetParent(child, hostHandle);
            FillHost(host, child);
            OutputWindowLogger.Debug(() => $"[pane] task manager {child} hosted in {hostHandle}");
        };

        host.SizeChanged += (s, e) => FillHost(host, child);

        // The browser owns the window it lends us and destroys it when it likes — opening another
        // chat is enough. Without this the host stays up as an empty rectangle.
        var watchdog = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        watchdog.Tick += (s, e) =>
        {
            if (IsWindow(child)) { return; }
            OutputWindowLogger.Debug(() => $"[pane] task manager {child} went away — closing host");
            host.Close();
        };
        watchdog.Start();

        host.Closed += (s, e) =>
        {
            watchdog.Stop();
            _taskManagerHost = null;
        };

        _taskManagerHost = host;
        host.Show();
    }

    private static void FillHost(Window host, IntPtr child)
    {
        var handle = new WindowInteropHelper(host).Handle;
        if (handle == IntPtr.Zero || !GetClientRect(handle, out var rect)) { return; }
        SetWindowPos(child, IntPtr.Zero, 0, 0, rect.Right, rect.Bottom, SwpSizeOnly);
    }

    private void Unhook()
    {
        _unhookTimer?.Stop();
        _unhookTimer = null;
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _winEventCallback = null;
    }

    /// <summary>Drop the hook with the control: the pane can be closed inside the seconds it stays
    /// armed, and user32 must not be left holding a callback into a dead object.</summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (VisualParent == null) { Unhook(); }
    }
}

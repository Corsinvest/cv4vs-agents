/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Chat.Pane;

/// <summary>
/// The chat's WebView2, forwarding the keys composition rendering drops and following the pane
/// into whatever window Visual Studio moves it to.
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

    internal event Action<string[]> HostFilesDropped;

    /// <summary>Logger for this control. XAML builds it with no constructor args, so it starts on
    /// <see cref="OutputWindowLogger.Global"/>; <see cref="ChatPaneControl"/> replaces it with the
    /// pane's own logger right after construction, the same fallback shape as a class that takes
    /// its logger as an optional constructor argument.</summary>
    internal OutputWindowLogger Log { get; set; } = OutputWindowLogger.Global;

    // public, not internal: XAML instantiates this by x:Name and needs a public default ctor.
    public ChatWebView()
    {
        AllowDrop = true;
        // Never let the control reach 0x0: WebView2CompositionControl's private SizeChanged
        // handler passes the size straight to Direct3D11CaptureFramePool.Recreate, which throws
        // E_INVALIDARG on zero and takes devenv down with it — the throw is inside WPF layout, so
        // nothing of ours can catch it (WebView2Feedback#5485, open through 1.0.3967.48). VS hands
        // the pane a 0x0 pass when an auto-hidden window is expanded. 1 DIP stays >= 1px at every
        // scale, and a 1px sliver of a collapsed pane is invisible.
        MinWidth = 1;
        MinHeight = 1;

        // See the FollowWindow region below for why these two are here.
        PresentationSource.AddSourceChangedHandler(this, OnPresentationSourceChanged);
        CoreWebView2InitializationCompleted += OnCoreWebView2InitializationCompleted;
    }

    // Preview, and Handled either way, or the drop tunnels on to VS and opens the file in an editor.
    // Handing it to the browser instead is not an option: the .NET wrapper exposes only DragLeave of
    // ICoreWebView2CompositionController3.
    protected override void OnPreviewDragOver(System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnPreviewDrop(System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            HostFilesDropped?.Invoke(paths);
        }
        e.Handled = true;
    }

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
        // Alt+Home/End is browser navigation (back/forward), not ours to take. Alt+Enter never
        // reaches here at all — VS turns it into the Properties command first, so it is claimed
        // in ChatPaneWindow alongside Esc and Ctrl+F.
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

    /// <summary>Makes a double click select the word rather than the whole paragraph.
    /// <para>WPF raises MouseDown → MouseUp → MouseDown → MouseUp → MouseDoubleClick for one double
    /// click, and the composition control forwards both MouseDown and MouseDoubleClick through
    /// SendMouseInput. So the browser gets three mousedown for two physical clicks, reads the third
    /// as a triple click and selects the block. Measured on the page: the second and third arrive
    /// with the SAME timeStamp — one click delivered twice, not an extra one invented.</para></summary>
    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        // Empty on purpose, and no base call: OnMouseDown has already sent this click, with its own
        // ClickCount, so the page still sees detail=2 and nothing needs the second delivery.
    }

    // --- Following the pane into whatever window VS moves it to -----------------------------
    //
    // WebView2CompositionControl gives its CoreWebView2Controller a ParentWindow once, the HWND
    // hosting the control at its very first Loaded, and never updates it afterwards (verified by
    // decompiling the SDK 1.0.3179.45 through 1.0.3967.48 — the last is what every known Visual
    // Studio redirects its own copy to at runtime, ignoring the version this project references).
    // Docking a pane is fine, because it never moves out of the main window. Floating one does:
    // VS reparents the pane's content into a separate FloatingWindow, so Win32 keyboard focus for
    // the browser still targets the OLD window. A click still lands — SendMouseInput's coordinates
    // are local to this control, not the parent — but the keys that follow reach whatever the old
    // window has focused instead of the page. This is WebView2Feedback#5398; there is no SDK fix.
    //
    // Fixed here by tracking PresentationSource changes and re-pointing ParentWindow ourselves.

    private static readonly FieldInfo _webview2BaseField =
        typeof(WebView2CompositionControl).GetField("m_webview2Base", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly PropertyInfo _controllerProperty =
        _webview2BaseField?.FieldType.GetProperty("CoreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
    private static bool _reflectionWarned;

    /// <summary>The live controller behind this control, or null before init completes, or if a
    /// future SDK ever reshapes these internals — reflection is the only way to reach either
    /// member, so a missing one degrades to today's stuck-on-the-first-window behavior instead of
    /// throwing.</summary>
    private CoreWebView2Controller Controller
    {
        get
        {
            if (_webview2BaseField == null || _controllerProperty == null)
            {
                if (!_reflectionWarned)
                {
                    _reflectionWarned = true;
                    Log.Warn("[webview] WebView2CompositionControl internals not found — a floating chat pane won't take typing");
                }
                return null;
            }
            try
            {
                var webview2Base = _webview2BaseField.GetValue(this);
                return webview2Base == null ? null : _controllerProperty.GetValue(webview2Base) as CoreWebView2Controller;
            }
            catch (Exception ex)
            {
                Log.LogException("ChatWebView.Controller", ex);
                return null;
            }
        }
    }

    // The HwndSource the controller is currently following, so a later move can find and drop
    // the hook installed on it below.
    private HwndSource _trackedSource;

    private void OnPresentationSourceChanged(object sender, SourceChangedEventArgs e)
    {
        // Fires when this control moves to a different top-level window — VS creating or
        // destroying the FloatingWindow around a floated/docked pane — and also when it is
        // detached entirely (auto-hide collapse, a hidden tab): NewSource is null then, and
        // there is nothing to follow until it reattaches.
        if (e.NewSource is HwndSource source) { FollowWindow(source); }
    }

    private void OnCoreWebView2InitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        // Covers a pane moved to another window while EnsureCoreWebView2Async was still running
        // (it blocks the UI thread for ~2s): the controller didn't exist yet for a SourceChanged
        // in that window to act on, so re-check the CURRENT source once it does.
        if (e.IsSuccess && PresentationSource.FromVisual(this) is HwndSource source) { FollowWindow(source); }
    }

    /// <summary>Point the controller at <paramref name="source"/> if it isn't already, and start
    /// watching that window for the move/destroy notifications the controller can't hear about
    /// on its own.</summary>
    private void FollowWindow(HwndSource source)
    {
        var controller = Controller;
        if (controller == null || source.Handle == IntPtr.Zero) { return; }

        try
        {
            if (controller.ParentWindow != source.Handle)
            {
                controller.ParentWindow = source.Handle;
                SyncBounds(controller, source);
                Log.Debug(() => $"[webview] controller parent -> 0x{source.Handle.ToInt64():X}");
            }
        }
        catch (Exception ex)
        {
            Log.LogException("ChatWebView.FollowWindow", ex);
            return;
        }

        WatchSource(source);
    }

    /// <summary>Recompute Bounds against the window the control was just re-parented to. Done by
    /// hand, against <paramref name="source"/>'s own root, rather than through the wrapper's
    /// private SyncControllerBounds(): that method trusts a window reference it cached at first
    /// Loaded and (on the SDK build every known Visual Studio runs) stops updating for good after
    /// the control's first Unloaded — exactly the state that just went stale.</summary>
    private void SyncBounds(CoreWebView2Controller controller, HwndSource source)
    {
        if (source.RootVisual is not UIElement root) { return; }
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var origin = TranslatePoint(new Point(0, 0), root);
        controller.Bounds = new System.Drawing.Rectangle(
            (int)(origin.X * dpi), (int)(origin.Y * dpi),
            (int)(ActualWidth * dpi), (int)(ActualHeight * dpi));
        controller.NotifyParentWindowPositionChanged();
    }

    private void WatchSource(HwndSource source)
    {
        if (ReferenceEquals(_trackedSource, source)) { return; }
        _trackedSource?.RemoveHook(OnParentWindowMessage);
        _trackedSource = source;
        source.AddHook(OnParentWindowMessage);
    }

    private const int WM_DESTROY = 0x0002;
    private const int WM_MOVE = 0x0003;

    /// <summary>WM_MOVE keeps popups and the context menu positioned correctly — the wrapper's own
    /// LocationChanged hook is dropped for good after the control's first Unloaded on the SDK
    /// build every known Visual Studio runs. WM_DESTROY guards the reverse hazard: a pane FIRST
    /// opened while floating has its only ParentWindow (the FloatingWindow) destroyed the moment
    /// it's docked, and Windows sends WM_DESTROY to a window while its children still exist —
    /// WM_NCDESTROY, which arrives after they're gone, would be too late to move out.</summary>
    private IntPtr OnParentWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOVE:
                try { Controller?.NotifyParentWindowPositionChanged(); }
                catch (Exception ex) { Log.LogException("ChatWebView.WM_MOVE", ex); }
                break;
            case WM_DESTROY:
                ParkUnderMainWindow(hwnd);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Move the controller onto the VS main window — which outlives every pane — before
    /// <paramref name="dyingHwnd"/> actually closes. It is a valid ParentWindow only briefly: the
    /// next SourceChanged (this control re-entering wherever it lands, docked or floating again)
    /// calls FollowWindow and moves it on.</summary>
    private void ParkUnderMainWindow(IntPtr dyingHwnd)
    {
        _trackedSource?.RemoveHook(OnParentWindowMessage);
        _trackedSource = null;

        var controller = Controller;
        if (controller == null) { return; }
        try
        {
            if (controller.ParentWindow != dyingHwnd) { return; } // already moved on
            var main = Win32Focus.MainWindowHandle();
            if (main == IntPtr.Zero) { return; }
            controller.ParentWindow = main;
            Log.Debug(() => $"[webview] parent 0x{dyingHwnd.ToInt64():X} closing — parked under the main window");
        }
        catch (Exception ex)
        {
            Log.LogException("ChatWebView.ParkUnderMainWindow", ex);
        }
    }

    /// <summary>Stop following window moves. Called from ChatPaneControl.DisposeCore: the main
    /// window's HwndSource outlives every pane and would otherwise keep this control's hook (and
    /// this control itself) alive after the pane is gone.</summary>
    internal void ReleaseWindowTracking()
    {
        PresentationSource.RemoveSourceChangedHandler(this, OnPresentationSourceChanged);
        CoreWebView2InitializationCompleted -= OnCoreWebView2InitializationCompleted;
        _trackedSource?.RemoveHook(OnParentWindowMessage);
        _trackedSource = null;
    }

    // Static: the browser has ONE task manager, so one owner serves every pane. A per-pane field
    // grew a spare renderer for each chat that had opened it, all of them then listed in the very
    // window they had opened.
    private static CoreWebView2Controller _taskManagerOwner;

    // The message-only window: a parent for things that must never be shown.
    private static readonly IntPtr HwndMessage = new(-3);

    /// <summary>The browser's own task manager — the Edge one, with live memory/CPU per process.
    /// It covers every pane on the user-data folder, panes in another VS instance included, which
    /// is what makes it worth having: a stray renderer or a process count that doesn't add up is a
    /// question about the whole browser, not about this pane.
    /// <para>Opened from a second controller, created windowed, because the window inherits the
    /// hosting mode of whoever asks: our own controller is a composition one, and the task manager
    /// it opens comes up with no usable frame — Windows reserves the caption space but Chromium
    /// paints over it, so no title bar ever shows (verified against SetWindowLong, RedrawWindow,
    /// resize, hide/show and reparenting). A windowed controller gets the window Chromium would
    /// have given a normal app. It shares this environment, so it is the same browser process and
    /// the same process list — it just never shows a page of its own.</para></summary>
    internal void OpenTaskManager() => _ = OpenTaskManagerAsync();

    private async Task OpenTaskManagerAsync()
    {
        try
        {
            var core = CoreWebView2;
            if (core == null) { return; }

            // The browser only ever has one task manager: a second call just raises its window.
            // HWND_MESSAGE is documented for exactly this — a WebView that never becomes visible —
            // and keeps the child window the controller creates out of our pane, where it would
            // otherwise land and take part in the z-order.
            if (_taskManagerOwner == null)
            {
                _taskManagerOwner = await core.Environment.CreateCoreWebView2ControllerAsync(HwndMessage);
                // It shows up in the very list it opens, and "WebView2: about:blank" says nothing
                // about why a second renderer is there. The task manager labels each row with the
                // document title, so give it one.
                _taskManagerOwner.CoreWebView2.NavigateToString(
                    "<!doctype html><title>cv4vs Agents — task manager host</title>");
            }

            _taskManagerOwner.CoreWebView2.OpenTaskManagerWindow();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"{nameof(ChatWebView)}.{nameof(OpenTaskManager)}", ex);
        }
    }

    /// <summary>Release the shared task-manager owner. Called when the last pane goes: it can't be
    /// tied to any single one, and closing it takes the task-manager window with it.</summary>
    internal static void CloseTaskManagerOwner()
    {
        if (_taskManagerOwner == null) { return; }
        try { _taskManagerOwner.Close(); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException($"{nameof(ChatWebView)}.CloseTaskManagerOwner", ex); }
        _taskManagerOwner = null;
    }
}

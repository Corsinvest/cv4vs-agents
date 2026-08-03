/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

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

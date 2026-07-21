/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Menu;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Imaging;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// Shared, kind-agnostic toolbar mounted on every session pane by PaneWindowBase,
/// driving the pane only through <see cref="IPaneControl"/>. Menus are defined in
/// XAML for VS-themed styling; dynamic items (open-panes list, kind-specific extras)
/// are filled in at open time and inherit the same styling.
/// </summary>
public partial class PaneToolbar : UserControl
{
    // Set once by Attach (called in PaneWindowBase's ctor, before the toolbar renders or any
    // handler can fire) and never cleared — non-null for every user interaction below.
    private IPaneControl _pane;
    // Count of pane-specific items appended to the More menu, so reopening clears just those
    // and leaves the static entries alone.
    private int _extrasCount;

    public PaneToolbar() => InitializeComponent();

    /// <summary>Wire the toolbar to its owning pane. Call once after the pane
    /// control is constructed. Per-pane actions (New session here, History) are
    /// disabled until the pane's process is ready.</summary>
    public void Attach(IPaneControl pane)
    {
        _pane = pane;
        pane.ReadyChanged += (_, _) => UpdateReadyState();
        pane.SessionTitleChanged += (_, _) => Dispatcher.Invoke(UpdateTitle);
        UpdateReadyState();
        UpdateTitle();
    }

    // ----- Session title (always an editable TextBox; border shows on hover/focus).
    //       Enter or focus-loss saves a rename, Esc reverts. -----

    // True while the title box has keyboard focus → don't let a background title
    // refresh (turn-end re-read) overwrite what the user is typing.
    private bool _titleFocused;

    /// <summary>Reflect the pane's current title. Hidden when the pane doesn't
    /// support a title (CLI) or there's no session yet (fresh chat). Skipped while
    /// the box is focused, so a turn-end refresh doesn't clobber the user's edit.</summary>
    private void UpdateTitle()
    {
        if (_titleFocused) { return; }
        var title = _pane.SupportsTitleEditing ? _pane.SessionTitle : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleHost.Visibility = Visibility.Collapsed;
            TitleBox.Text = string.Empty;
            return;
        }
        TitleHost.Visibility = Visibility.Visible;
        TitleBox.Text = title;
    }

    private void OnTitleGotFocus(object sender, RoutedEventArgs e) => _titleFocused = true;

    private void OnTitleKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            CommitTitle();
            System.Windows.Input.Keyboard.ClearFocus(); // drop focus → commit done, border off
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            _titleFocused = false;
            UpdateTitle();                               // revert to the stored title
            System.Windows.Input.Keyboard.ClearFocus();
        }
    }

    private void OnTitleLostFocus(object sender, RoutedEventArgs e)
    {
        CommitTitle();
        _titleFocused = false;
    }

    private void CommitTitle()
    {
        var newTitle = TitleBox.Text?.Trim();
        if (!string.IsNullOrEmpty(newTitle) && newTitle != _pane.SessionTitle)
        {
            _pane.RenameSession(newTitle);
        }
    }

    /// <summary>Enable/disable the per-pane actions based on <see cref="IPaneControl.IsReady"/>.</summary>
    private void UpdateReadyState()
    {
        var ready = _pane.IsReady;
        BtnNewSession.IsEnabled = ready;
        BtnSessionHistory.IsEnabled = ready;
    }

    // The new pane inherits this pane's profile — same environment continues.
    private void OnNew_Click(object sender, RoutedEventArgs e)
        => PaneLauncher.OpenNew(DefaultKind(), _pane.Entry.Profile);

    private void OnNewDropdown_Click(object sender, RoutedEventArgs e)
        => OpenContextMenu(BtnNewDropdown);

    private void OnNewChat_Click(object sender, RoutedEventArgs e)
        => PaneLauncher.OpenNew(PaneKind.Chat, _pane.Entry.Profile);

    private void OnNewCli_Click(object sender, RoutedEventArgs e)
        => PaneLauncher.OpenNew(PaneKind.Cli, _pane.Entry.Profile);

    private static PaneKind DefaultKind()
        => AgentsOptions.General.DefaultNewSession == NewSessionKind.Cli
            ? PaneKind.Cli : PaneKind.Chat;

    private void OnPanes_Click(object sender, RoutedEventArgs e)
    {
        PanesMenu.Items.Clear();
        foreach (var entry in PaneRegistry.Instance.Entries.OrderBy(en => en.SeqNo))
        {
            var captured = entry;
            var item = new MenuItem
            {
                Header = entry.Title,
                Icon = new CrispImage
                {
                    Width = 16,
                    Height = 16,
                    Moniker = entry.Kind == PaneKind.Cli
                                ? KnownMonikers.Console
                                : KnownMonikers.Comment,
                },
            };

            item.Click += (_, _) => captured.ActivateAction?.Invoke();
            PanesMenu.Items.Add(item);
        }

        if (PanesMenu.Items.Count == 0)
        {
            PanesMenu.Items.Add(new MenuItem { Header = "(no open panes)", IsEnabled = false });
        }
        OpenContextMenu(BtnPanes);
    }


    // ----- History (this pane) -----

    private void OnHistory_Click(object sender, RoutedEventArgs e) => ShowSessionHistory();

    /// <summary>Open the session picker for this pane. Public because the chat's
    /// "Resume conversation" command reaches it through the bridge, not just the
    /// toolbar button — same popup either way.</summary>
    // The live session-picker popup, so Esc (which VS routes through IOleCommandTarget, never
    // reaching the popup) can dismiss it. Null when closed.
    private Popup _historyPopup;

    /// <summary>Close the session picker if open; true when it was. Called by the pane's Esc
    /// handler before it forwards Esc anywhere else.</summary>
    public bool DismissSessionHistory()
    {
        if (_historyPopup?.IsOpen != true) { return false; }
        _historyPopup.IsOpen = false;
        _historyPopup = null;
        _pane.FocusInput();
        return true;
    }

    public void ShowSessionHistory()
    {
        var picker = new SessionManagerControl(ClaudePaths.ForProfile(_pane.Entry.Profile), _pane.Entry.WorkingDirectory, _pane.Entry.ActiveSessionId);
        var popup = new Popup
        {
            PlacementTarget = BtnSessionHistory,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Focusable = true,
            // 1px border so the popup detaches from the pane underneath (was SessionPickerPopup's job).
            Child = new Border
            {
                Width = 480,
                BorderThickness = new Thickness(1),
                BorderBrush = SystemColors.ActiveBorderBrush,
                Child = picker,
            },
        };
        // null sessionId from the picker = "new conversation here".
        picker.SessionSelected += sessionId =>
        {
            popup.IsOpen = false;
            if (string.IsNullOrEmpty(sessionId)) { _pane.NewSession(); }
            else { _pane.LoadSession(sessionId); }
            _pane.FocusInput();  // user picked a conversation → let them type
        };
        picker.Cancelled += () =>
        {
            popup.IsOpen = false;
            _pane.FocusInput();
        };
        popup.Closed += (_, _) => { if (ReferenceEquals(_historyPopup, popup)) { _historyPopup = null; } };
        _historyPopup = popup;
        popup.IsOpen = true;
        // Opened from the chat command the caret sits in WebView2 — a separate HWND. Blurring it
        // only clears the DOM caret (and asynchronously at that), so Win32 focus has to be pulled
        // onto the popup's own window or the search box takes no typing.
        _pane.BlurInput();
        // At Render priority the popup has its own HwndSource; before that there's nothing to
        // focus. FocusSearch runs at the same priority, so it lands right after this.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            var hwnd = (PresentationSource.FromVisual(picker) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) { SetFocus(hwnd); }
        }));
        picker.FocusSearch();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    // ----- New session (this pane) -----

    // Fresh conversation in THIS pane (vs. BtnNew, which opens a new pane).
    private void OnNewSession_Click(object sender, RoutedEventArgs e)
    {
        _pane.NewSession();
        _pane.FocusInput();
    }

    // ----- More (⋯): static items in XAML; pane extras inserted at top -----

    private void OnMore_Click(object sender, RoutedEventArgs e)
    {
        // Pane-specific extras are appended after the static items. Drop the ones added last
        // time, then re-add for the current pane.
        for (int i = 0; i < _extrasCount; i++)
        {
            MoreMenu.Items.RemoveAt(MoreMenu.Items.Count - 1);
        }
        _extrasCount = 0;

        var extras = _pane.MoreMenuActions?.ToList();
        MoreExtrasSeparator.Visibility = extras is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (extras is { Count: > 0 })
        {
            foreach (var a in extras)
            {
                var captured = a;
                var item = new MenuItem { Header = a.Label, Icon = MonikerImage(a.IconId) };
                item.Click += (_, _) => captured.Invoke?.Invoke();
                MoreMenu.Items.Add(item);
                _extrasCount++;
            }
        }
        OpenContextMenu(BtnMore);
    }

    private void OnMenu_Info(object sender, RoutedEventArgs e) => _pane.ShowSessionInfo();

    private void OnMenu_SessionsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            // _pane is always attached here (the toolbar lives inside a live pane) and the entry's
            // workdir is always set (solution folder or user profile) — no null/empty fallbacks needed.
            var paths = ClaudePaths.ForProfile(_pane.Entry.Profile);
            var folder = paths.SessionFolder(_pane.Entry.WorkingDirectory);
            if (!System.IO.Directory.Exists(folder)) { folder = paths.ProjectsFolder; }
            if (!System.IO.Directory.Exists(folder)) { System.IO.Directory.CreateDirectory(folder); }
            ShellHelpers.OpenExternal(folder);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("PaneToolbar.SessionsFolder", ex); }
    }

    private static CrispImage MonikerImage(string iconId)
        => new()
        {
            Width = 16,
            Height = 16,
            Moniker = iconId switch
            {
                "DevTools" => KnownMonikers.JSConsole,
                "Refresh" => KnownMonikers.Refresh,
                "Info" => KnownMonikers.StatusInformation,
                _ => KnownMonikers.Code,
            }
        };

    private static void OpenContextMenu(Button btn)
    {
        if (btn.ContextMenu == null) { return; }
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }
}

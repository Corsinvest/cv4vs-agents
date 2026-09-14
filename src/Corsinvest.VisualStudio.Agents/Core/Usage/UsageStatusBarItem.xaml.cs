/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>The status bar item: the mascot, then "Claude: 5h 44% · 7d 10%" with a thin bar under each
/// figure, and on click the popup with the rest. It draws what <see cref="UsageStatusService"/> holds and
/// asks it for nothing but a refresh when the popup opens.</summary>
public partial class UsageStatusBarItem : UserControl
{
    // A mouse-down outside the popup closes it before the click lands here; this long after that close
    // the click is taken as "close", not "open again".
    private const int ReopenGuardMs = 250;

    private readonly Popup _popup;
    private readonly UsagePopupControl _popupContent;
    private int _popupClosedAt;
    private IntPtr _focusBeforePopup;
    private bool _restoreFocusOnClose;

    public UsageStatusBarItem()
    {
        InitializeComponent();

        _popupContent = new UsagePopupControl();
        _popupContent.CloseRequested += restoreFocus =>
        {
            _restoreFocusOnClose = restoreFocus;
            ClosePopup();
        };
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Top,
            VerticalOffset = -2,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Focusable = true,
            Child = _popupContent,
        };
        _popup.Opened += (_, _) => OnPopupStateChanged();
        _popup.Closed += (_, _) =>
        {
            _popupClosedAt = Environment.TickCount;
            OnPopupStateChanged();
            // Esc took focus away from wherever the user was typing; a click elsewhere put it somewhere itself.
            if (_restoreFocusOnClose && _focusBeforePopup != IntPtr.Zero) { SetFocus(_focusBeforePopup); }
            _restoreFocusOnClose = false;
        };

        Loaded += (_, _) =>
        {
            UsageStatusService.Instance.Changed += Render;
            Render();
        };
        Unloaded += (_, _) => UsageStatusService.Instance.Changed -= Render;
        MouseEnter += (_, _) => UpdateHighlight();
        MouseLeave += (_, _) => UpdateHighlight();
        MouseLeftButtonUp += OnClick;
    }

    public void ClosePopup() => _popup.IsOpen = false;

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        e.Handled = true;
        if (unchecked(Environment.TickCount - _popupClosedAt) < ReopenGuardMs) { return; }
        OpenPopup();
    }

    private void OpenPopup()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _focusBeforePopup = GetFocus();
        _popupContent.Render(UsageStatusService.Instance.Current, DateTimeOffset.Now);
        _popup.IsOpen = true;
        UsageStatusService.Instance.OnPopupOpened();
        // The popup is a window of its own: focused, it hears Esc instead of the editor behind it. Only at
        // Render priority does it have the HwndSource to focus.
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            if (!_popup.IsOpen) { return; }
            if (PresentationSource.FromVisual(_popupContent) is HwndSource source) { SetFocus(source.Handle); }
            _popupContent.Focus();
        }));
    }

    private void OnPopupStateChanged()
    {
        UpdateHighlight();
        // A tooltip over the open popup would only cover it.
        ToolTipService.SetIsEnabled(this, !_popup.IsOpen);
    }

    private void UpdateHighlight() => Highlight.Opacity = _popup.IsOpen ? 0.2 : IsMouseOver ? 0.12 : 0;

    private void Render()
    {
        var snapshot = UsageStatusService.Instance.Current;
        var now = DateTimeOffset.Now;
        var segments = UsageStatusFormat.Segments(snapshot, now);

        ProfileText.Text = segments.Count > 0 ? snapshot.ProfileName + ":"
            : snapshot.State == UsageAvailability.Unavailable ? snapshot.ProfileName + ": —"
            : snapshot.ProfileName;

        SegmentsPanel.Children.Clear();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) { SegmentsPanel.Children.Add(new TextBlock { Text = " · ", VerticalAlignment = VerticalAlignment.Center }); }
            SegmentsPanel.Children.Add(BuildSegment(segments[i], i == 0));
        }

        // Old numbers, and a profile with none to give, step back rather than vanish.
        ContentPanel.Opacity = snapshot.IsStale || snapshot.State is UsageAvailability.NoPlan or UsageAvailability.CliMissing ? 0.6 : 1;
        ToolTip = UsageStatusFormat.Tooltip(snapshot, now, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        AutomationProperties.SetName(this, UsageStatusFormat.StatusText(snapshot, now));
        if (_popup.IsOpen) { _popupContent.Render(snapshot, now); }
    }

    private UIElement BuildSegment(UsageSegment segment, bool first)
    {
        var cell = new Grid { Margin = new Thickness(first ? 4 : 0, 0, 0, 0) };
        cell.Children.Add(new TextBlock
        {
            Text = segment.Text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = segment.Level == UsageLevel.Critical ? FontWeights.SemiBold : FontWeights.Normal,
        });

        // The bar runs under the text, filled to the percentage: two star columns share the width.
        var bar = new Grid { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segment.Percent, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - segment.Percent, GridUnitType.Star) });
        var track = new Rectangle { Opacity = 0.25 };
        track.SetBinding(Shape.FillProperty, ForegroundBinding());
        Grid.SetColumnSpan(track, 2);
        var fill = new Rectangle();
        SetLevelFill(fill, segment.Level);
        bar.Children.Add(track);
        bar.Children.Add(fill);
        cell.Children.Add(bar);
        return cell;
    }

    private void SetLevelFill(Shape shape, UsageLevel level)
    {
        switch (level)
        {
            case UsageLevel.Critical:
                shape.SetResourceReference(Shape.FillProperty, EnvironmentColors.VizSurfaceRedMediumBrushKey);
                break;
            case UsageLevel.Warning:
                shape.SetResourceReference(Shape.FillProperty, EnvironmentColors.VizSurfaceGoldMediumBrushKey);
                break;
            default:
                // The bar's own text colour follows it through the building/debugging states.
                shape.SetBinding(Shape.FillProperty, ForegroundBinding());
                shape.Opacity = 0.85;
                break;
        }
    }

    private Binding ForegroundBinding() => new(nameof(Foreground)) { Source = this };

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

#pragma warning disable CS0618

using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>The Keep and Undo buttons over the hunk the caret is in, in each view of a review's window: drawn in the
/// editor's own adornment layer, right-aligned above the hunk's first line, or at the top of the view when that line has
/// scrolled out of it. Shown only while the view has the keyboard, as Copilot's are, so a window nobody is working in has
/// nothing on it.
/// <para>No MEF: the layer is one of the editor's own (<see cref="PredefinedAdornmentLayers.Caret"/>), so each adornment
/// carries a tag of this class's and is removed by it, never with the layer's other contents.</para></summary>
internal sealed class HunkAdornments : IDisposable
{
    private readonly BaselineDocumentController _controller;
    private readonly IVsDifferenceCodeWindow2 _window;
    private readonly Dictionary<IWpfTextView, ViewAdornment> _views = new();
    private IDifferenceViewer _viewer;
    private bool _disposed;

    private HunkAdornments(BaselineDocumentController controller, IVsDifferenceCodeWindow2 window)
    {
        _controller = controller;
        _window = window;
        _viewer = ((IVsDifferenceCodeWindow)window).DifferenceViewer;
        _viewer.ViewModeChanged += OnViewModeChanged;
    }

    /// <summary>Null when the window cannot be drawn on: the review then has the commands and the list, without the buttons.</summary>
    public static HunkAdornments TryCreate(BaselineDocumentController controller, IVsDifferenceCodeWindow2 window)
    {
        try
        {
            var adornments = new HunkAdornments(controller, window);
            adornments.Refresh();
            return adornments;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("HunkAdornments.TryCreate", ex);
            return null;
        }
    }

    /// <summary>Bring the buttons up to date with the caret and the difference: for every view the window has now.</summary>
    public void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) { return; }
        try
        {
            EnsureViews();
            foreach (var view in _views.Values) { view.Update(); }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("HunkAdornments.Refresh", ex);
        }
    }

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) { return; }
        _disposed = true;
        try { _viewer.ViewModeChanged -= OnViewModeChanged; }
        catch (Exception) { /* a viewer that is going away */ }
        foreach (var view in _views.Values) { view.Dispose(); }
        _views.Clear();
    }

    private void OnViewModeChanged(object sender, EventArgs e) => Refresh();

    // The views a window has depend on its mode: one inline view, or a left and a right. Each gets its own adornment, and one
    // whose view has gone is let go.
    private void EnsureViews()
    {
        var viewer = (IDifferenceViewer3)((IVsDifferenceCodeWindow)_window).DifferenceViewer;
        var wpf = (IWpfDifferenceViewer)viewer;
        var live = new List<IWpfTextView>();
        if (((IDifferenceViewer2)viewer).InlineViewExists && wpf.InlineView is not null) { live.Add(wpf.InlineView); }
        if (((IDifferenceViewer2)viewer).LeftViewExists && wpf.LeftView is not null) { live.Add(wpf.LeftView); }
        if (((IDifferenceViewer2)viewer).RightViewExists && wpf.RightView is not null) { live.Add(wpf.RightView); }

        foreach (var gone in _views.Keys.Where(v => !live.Contains(v)).ToList())
        {
            _views[gone].Dispose();
            _views.Remove(gone);
        }
        foreach (var view in live)
        {
            if (!_views.ContainsKey(view)) { _views[view] = new ViewAdornment(_controller, view); }
        }
    }

    /// <summary>The buttons in one view.</summary>
    private sealed class ViewAdornment : IDisposable
    {
        private readonly BaselineDocumentController _controller;
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly object _tag = new();
        private HunkButtons _buttons;
        private Hunk _hunk;
        private bool _focused;
        private bool _disposed;

        public ViewAdornment(BaselineDocumentController controller, IWpfTextView view)
        {
            _controller = controller;
            _view = view;
            _layer = view.GetAdornmentLayer(PredefinedAdornmentLayers.Caret);
            view.GotAggregateFocus += OnFocusChanged;
            view.LostAggregateFocus += OnFocusChanged;
            view.Closed += OnClosed;
            OnFocusChanged(null, null);
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _view.GotAggregateFocus -= OnFocusChanged;
            _view.LostAggregateFocus -= OnFocusChanged;
            _view.Closed -= OnClosed;
            Listen(false);
            Hide();
        }

        private void OnClosed(object sender, EventArgs e) => Dispose();

        private void OnFocusChanged(object sender, EventArgs e)
        {
            var focused = _view.HasAggregateFocus;
            if (focused == _focused) { return; }
            _focused = focused;
            Listen(focused);
            if (focused) { Update(); } else { Hide(); }
        }

        private bool _listening;

        private void Listen(bool on)
        {
            if (on == _listening) { return; }
            _listening = on;
            if (on)
            {
                _view.Caret.PositionChanged += OnCaretMoved;
                _view.LayoutChanged += OnLayoutChanged;
            }
            else
            {
                _view.Caret.PositionChanged -= OnCaretMoved;
                _view.LayoutChanged -= OnLayoutChanged;
            }
        }

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => _view.QueuePostLayoutAction(Update);
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Update();

        public void Update()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { UpdateCore(); }
            catch (Exception ex)
            {
                // The text of a view that is being laid out cannot always be asked: the next layout draws them.
                OutputWindowLogger.Global.Debug(() => $"[changes] the buttons were not drawn: {ex.Message}");
                Hide();
            }
        }

        private void UpdateCore()
        {
            if (_disposed || !_focused || _view.IsClosed || _controller.Exited || _controller.IsBusy) { Hide(); return; }
            if (!HunkLocator.TryGetHunkAtCaret(_view, out _hunk) || !HunkLocator.TryGetModel(_view, out var model)) { Hide(); return; }
            // Buttons over a hunk that an edit has already moved would act on something else.
            if (!_controller.IsCurrent(_hunk)) { Hide(); return; }
            var difference = HunkLocator.Current(model);
            var anchor = HunkLocator.AnchorIn(_view, model, difference, _hunk);
            if (anchor is null) { Hide(); return; }

            _buttons ??= new HunkButtons(Keep, Undo);
            _buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Above the hunk's first line when there is room for it, else at the top of the view.
            var top = _view.ViewportTop;
            var clipped = true;
            var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(anchor.Value);
            if (line is not null)
            {
                var above = line.Top - _buttons.DesiredSize.Height;
                if (above > top)
                {
                    top = above;
                    clipped = false;
                }
            }
            else if (anchor.Value.Position >= _view.TextViewLines.FormattedSpan.End.Position)
            {
                Hide();
                return;
            }

            Canvas.SetTop(_buttons, top);
            Canvas.SetLeft(_buttons, Math.Max(0, _view.ViewportWidth - _buttons.DesiredSize.Width));
            Hide();
            if (clipped)
            {
                _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, _tag, _buttons, null);
            }
            else
            {
                _layer.AddAdornment(new SnapshotSpan(anchor.Value, 0), _tag, _buttons);
            }
        }

        private void Hide()
        {
            try { _layer.RemoveAdornmentsByTag(_tag); }
            catch (Exception) { /* a view that is closing */ }
        }

        private void Keep() => Run(h => _controller.KeepHunkAsync(h, _view));
        private void Undo() => Run(h => _controller.UndoHunkAsync(h, _view));

        // The hunk the caret is in when the button is pressed, not the one it was in when the buttons were drawn: a click on a
        // button does not move the caret, but the difference may have been recomputed since.
        private void Run(Func<Hunk, System.Threading.Tasks.Task<bool>> action)
        {
            var hunk = HunkLocator.TryGetHunkAtCaret(_view, out var now) ? now : _hunk;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () => { await action(hunk); }).FileAndForget("HunkAdornments.Click");
        }
    }

    /// <summary>The two buttons. Plain WPF in the editor's own theme, not focusable: pressing one must not take the keyboard
    /// from the text, or the buttons would go the moment they are used.</summary>
    private sealed class HunkButtons : Border
    {
        public HunkButtons(Action keep, Action undo)
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(3);
            Padding = new Thickness(1);
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Button("Keep", "Keep this change: accept it as it is", keep),
                    Button("Undo", "Undo this change: put back the text from before Claude's change", undo),
                },
            };
        }

        private static Button Button(string text, string tip, Action click)
        {
            var button = new Button
            {
                Content = text,
                ToolTip = tip,
                Focusable = false,
                Margin = new Thickness(1),
                Padding = new Thickness(8, 1, 8, 1),
            };
            button.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            button.Click += (_, _) => click();
            return button;
        }
    }
}

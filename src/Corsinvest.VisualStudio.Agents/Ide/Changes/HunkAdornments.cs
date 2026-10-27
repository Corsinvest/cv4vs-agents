/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>The Keep and Undo buttons over the hunk the caret is in, in each view of a review's window: right-aligned above the
/// hunk's first line, or at the top of the view when that line has scrolled out of it. Shown only while the view has the
/// keyboard, as Copilot's are, so a window nobody is working in has nothing on it.
/// <para>No MEF, so no layer of our own in the editor. The buttons are a WPF adorner over the view (the layer the editor's own
/// margins use for the same purpose), and not an adornment of the text view's "Caret" layer, which is the obvious place and the
/// wrong one: the editor blinks the caret by changing the opacity of that whole layer, so the buttons would blink with it. Where
/// a view has no adorner layer the "Caret" layer is the fallback, blinking and all.</para></summary>
internal sealed class HunkAdornments : IDisposable
{
    private readonly BaselineDocumentController _controller;
    private readonly Dictionary<IWpfTextView, ViewAdornment> _views = new();
    private readonly IWpfDifferenceViewer _viewer;
    private bool _disposed;

    // The viewer of the view the review began in, not the window's active pane's: in a split window the two are not the same
    // viewer, and the review is the one that began where it did.
    private HunkAdornments(BaselineDocumentController controller)
    {
        _controller = controller;
        _viewer = controller.Viewer;
        _viewer.ViewModeChanged += OnViewModeChanged;
    }

    /// <summary>Null when the window cannot be drawn on: the review then has the commands and the list, without the buttons.</summary>
    public static HunkAdornments TryCreate(BaselineDocumentController controller)
    {
        try
        {
            var adornments = new HunkAdornments(controller);
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
        var wpf = _viewer;
        var two = (IDifferenceViewer2)wpf;
        var live = new List<IWpfTextView>();
        if (two.InlineViewExists && wpf.InlineView is not null) { live.Add(wpf.InlineView); }
        if (two.LeftViewExists && wpf.LeftView is not null) { live.Add(wpf.LeftView); }
        if (two.RightViewExists && wpf.RightView is not null) { live.Add(wpf.RightView); }

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

    /// <summary>Where the buttons go, in the document's coordinates as the text view's own layers have them (the viewport's
    /// origin is <c>ViewportLeft</c> and <c>ViewportTop</c> in them). <see cref="Clipped"/>: the hunk's first line is above what
    /// is shown, and the buttons sit at the top of the view instead of above that line.</summary>
    private readonly struct Placement : IEquatable<Placement>
    {
        public Placement(bool clipped, double top, double left, SnapshotPoint anchor)
        {
            Clipped = clipped;
            Top = top;
            Left = left;
            Anchor = anchor;
        }

        public bool Clipped { get; }
        public double Top { get; }
        public double Left { get; }
        public SnapshotPoint Anchor { get; }

        public bool Equals(Placement other)
            => Clipped == other.Clipped && Top == other.Top && Left == other.Left && Anchor.Position == other.Anchor.Position;

        public override bool Equals(object obj) => obj is Placement other && Equals(other);
        public override int GetHashCode() => Anchor.Position;
    }

    /// <summary>Somewhere to put the buttons of one view.</summary>
    private interface IButtonsPresenter : IDisposable
    {
        /// <summary>Put the buttons there, or move them there. False when they could not be shown.</summary>
        bool Show(HunkButtons buttons, Placement place);

        void Hide();
    }

    /// <summary>The buttons in one view.</summary>
    private sealed class ViewAdornment : IDisposable
    {
        private readonly BaselineDocumentController _controller;
        private readonly IWpfTextView _view;
        private IButtonsPresenter _presenter;
        private HunkButtons _buttons;
        private Hunk _hunk;
        private bool _focused;
        private bool _disposed;
        private bool _listening;

        public ViewAdornment(BaselineDocumentController controller, IWpfTextView view)
        {
            _controller = controller;
            _view = view;
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
            try { _presenter?.Dispose(); }
            catch (Exception) { /* a view that is closing */ }
            _presenter = null;
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

            // The right edge of what is shown is the viewport's right, and not its width, which only agrees with it until the
            // view has been scrolled sideways.
            var left = Math.Max(_view.ViewportLeft, _view.ViewportRight - _buttons.DesiredSize.Width);

            _presenter ??= (IButtonsPresenter)AdornerPresenter.TryCreate(_view) ?? new CaretLayerPresenter(_view);
            if (!_presenter.Show(_buttons, new Placement(clipped, top, left, anchor.Value))) { Hide(); }
        }

        private void Hide() => _presenter?.Hide();

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

    /// <summary>The buttons as a WPF adorner of the text view: in the view's own coordinates, so a scroll moves nothing but the
    /// numbers they are set from, and one adorner for as long as the view has the keyboard, moved and not taken out and put back
    /// (a layout comes with every difference the editor works out, and one in the middle of a click would lose it).</summary>
    private sealed class AdornerPresenter : IButtonsPresenter
    {
        private readonly IWpfTextView _view;
        private readonly AdornerLayer _layer;
        private ButtonsAdorner _adorner;

        private AdornerPresenter(IWpfTextView view, AdornerLayer layer)
        {
            _view = view;
            _layer = layer;
        }

        /// <summary>Null when the view is not in a tree that has an adorner layer.</summary>
        public static AdornerPresenter TryCreate(IWpfTextView view)
        {
            var layer = AdornerLayer.GetAdornerLayer(view.VisualElement);
            if (layer is null)
            {
                OutputWindowLogger.Global.Info("[changes] the editor view has no adorner layer here: the buttons are drawn in its caret layer, where they blink with the caret");
                return null;
            }
            return new AdornerPresenter(view, layer);
        }

        public bool Show(HunkButtons buttons, Placement place)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_adorner is null)
            {
                _adorner = new ButtonsAdorner(_view.VisualElement, buttons);
                _layer.Add(_adorner);
            }
            // The buttons are shown and hidden, not the adorner: the adorner layer sets the visibility of its adorners itself.
            buttons.Visibility = Visibility.Visible;
            _adorner.MoveTo(place.Left - _view.ViewportLeft, place.Top - _view.ViewportTop);
            return true;
        }

        public void Hide()
        {
            if (_adorner is not null) { _adorner.Buttons.Visibility = Visibility.Collapsed; }
        }

        public void Dispose()
        {
            var adorner = _adorner;
            _adorner = null;
            if (adorner is null) { return; }
            _layer.Remove(adorner);
            adorner.Detach();
        }
    }

    /// <summary>Hosts the buttons at a point of the adorned view. Takes up the view, draws nothing of its own and so catches no
    /// mouse input that is not for the buttons.</summary>
    private sealed class ButtonsAdorner : Adorner
    {
        private readonly HunkButtons _buttons;
        private Point _at;

        public ButtonsAdorner(UIElement adorned, HunkButtons buttons) : base(adorned)
        {
            _buttons = buttons;
            AddLogicalChild(buttons);
            AddVisualChild(buttons);
        }

        public HunkButtons Buttons => _buttons;

        public void MoveTo(double x, double y)
        {
            var at = new Point(x, y);
            if (at == _at) { return; }
            _at = at;
            InvalidateArrange();
        }

        public void Detach()
        {
            RemoveVisualChild(_buttons);
            RemoveLogicalChild(_buttons);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _buttons;

        protected override IEnumerator LogicalChildren
        {
            get { yield return _buttons; }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _buttons.Arrange(new Rect(_at, _buttons.DesiredSize));
            return finalSize;
        }
    }

    /// <summary>The fallback where the view has no adorner layer: an adornment of the text view's "Caret" layer, tagged so that it
    /// is removed by this and never with the layer's other contents. The editor blinks the caret by changing the opacity of that
    /// layer, so these blink too. Taken out and put back when it moves, and left alone while nothing does.</summary>
    private sealed class CaretLayerPresenter : IButtonsPresenter
    {
        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly object _tag = new();
        private bool _placed;
        private Placement? _last;

        public CaretLayerPresenter(IWpfTextView view)
        {
            _view = view;
            _layer = view.GetAdornmentLayer(PredefinedAdornmentLayers.Caret);
        }

        public bool Show(HunkButtons buttons, Placement place)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Already there, and nothing has moved: leave them.
            if (_placed && _last.HasValue && _last.Value.Equals(place)) { return true; }

            Hide();
            Canvas.SetTop(buttons, place.Top);
            Canvas.SetLeft(buttons, place.Left);
            _placed = place.Clipped
                ? _layer.AddAdornment(AdornmentPositioningBehavior.ViewportRelative, null, _tag, buttons, OnRemovedByLayer)
                : _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, new SnapshotSpan(place.Anchor, 0), _tag, buttons, OnRemovedByLayer);
            _last = _placed ? place : null;
            return _placed;
        }

        // The layer takes an adornment away by itself when the text it is beside is laid out again.
        private void OnRemovedByLayer(object tag, UIElement element)
        {
            _placed = false;
            _last = null;
        }

        public void Hide()
        {
            _placed = false;
            _last = null;
            try { _layer.RemoveAdornmentsByTag(_tag); }
            catch (Exception) { /* a view that is closing */ }
        }

        public void Dispose() => Hide();
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

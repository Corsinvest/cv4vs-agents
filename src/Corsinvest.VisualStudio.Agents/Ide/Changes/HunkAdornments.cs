/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Corsinvest.VisualStudio.Agents.Ide.Changes;

/// <summary>What a review draws over its views: the Keep and Undo buttons over the hunk the caret is in, right-aligned above the
/// hunk's first line (or at the top of the view when that line has scrolled out of it), and a floating panel at the bottom of the
/// view with the way round the review: the previous and next change, Keep all and Undo all for the file, and the previous and
/// next file of the list.
/// <para>No MEF, so no layer of our own in the editor. Both are WPF adorners over the view (the layer the editor's own margins
/// use for the same purpose), and not adornments of the text view's "Caret" layer, which is the obvious place and the wrong one:
/// the editor blinks the caret by changing the opacity of that whole layer, so they would blink with it.</para>
/// <para>The adorner layer is the main window's, and WPF takes the adorners of an element out of it as soon as the element has left
/// the visual tree, which is what a tab that is not shown does. Nothing puts them back: they are put back by the first update
/// that finds them gone.</para></summary>
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

    /// <summary>Bring the buttons and the panel up to date with the caret and the difference: for every view the window has now.</summary>
    public void Refresh()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed) { return; }
        try
        {
            EnsureViews();
            foreach (var view in _views.Values.ToList()) { view.Update(); }
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
        foreach (var view in _views.Values.ToList()) { view.Dispose(); }
        _views.Clear();
    }

    private void OnViewModeChanged(object sender, EventArgs e) => Refresh();

    // The buttons are on the view the review is shown in, whether or not it has the keyboard (the person may be in the chat), and on
    // any other of its views that has it: in a side-by-side window the caret that matters is the one that is being used.
    private bool WantsButtons(IWpfTextView view)
        => view.HasAggregateFocus
           || (ReferenceEquals(view, _controller.ShownView()) && !_views.Keys.Any(v => v.HasAggregateFocus));

    // One panel for the review, on the view it is shown in.
    private bool WantsPanel(IWpfTextView view) => ReferenceEquals(view, _controller.ShownView());

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
            if (!_views.ContainsKey(view)) { _views[view] = new ViewAdornment(this, _controller, view); }
        }
    }

    /// <summary>The buttons and the panel of one view.</summary>
    private sealed class ViewAdornment : IDisposable
    {
        private const double PanelMargin = 14;

        private readonly HunkAdornments _owner;
        private readonly BaselineDocumentController _controller;
        private readonly IWpfTextView _view;
        private readonly AdornerSlot _buttonsSlot;
        private readonly AdornerSlot _panelSlot;
        private readonly HunkButtons _buttons;
        private readonly ReviewPanel _panel;
        private Hunk _hunk;
        private readonly HashSet<string> _failed = new();
        private bool _disposed;
        private bool _noLayerLogged;

        public ViewAdornment(HunkAdornments owner, BaselineDocumentController controller, IWpfTextView view)
        {
            _owner = owner;
            _controller = controller;
            _view = view;
            _buttons = new HunkButtons(Keep, Undo);
            _panel = new ReviewPanel(
                () => Run(() => { controller.GoToPreviousHunk(view); return System.Threading.Tasks.Task.CompletedTask; }),
                () => Run(() => { controller.GoToNextHunk(view); return System.Threading.Tasks.Task.CompletedTask; }),
                () => Run(() => controller.KeepAllAsync()),
                () => Run(() => controller.UndoAllAsync()),
                () => Run(() => controller.GoToFileAsync(-1)),
                () => Run(() => controller.GoToFileAsync(1)));
            _buttonsSlot = new AdornerSlot(view, _buttons);
            _panelSlot = new AdornerSlot(view, _panel);

            // Everything that can change what is shown, whether or not the view has the keyboard: a tab that comes back, a window that
            // is resized, a caret that moves, a difference that is worked out again.
            view.GotAggregateFocus += OnChanged;
            view.LostAggregateFocus += OnChanged;
            view.Caret.PositionChanged += OnCaretMoved;
            view.LayoutChanged += OnLayoutChanged;
            view.VisualElement.Loaded += OnLoaded;
            view.VisualElement.IsVisibleChanged += OnVisibleChanged;
            view.Closed += OnClosed;
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            _view.GotAggregateFocus -= OnChanged;
            _view.LostAggregateFocus -= OnChanged;
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.VisualElement.Loaded -= OnLoaded;
            _view.VisualElement.IsVisibleChanged -= OnVisibleChanged;
            _view.Closed -= OnClosed;
            try
            {
                _buttonsSlot.Dispose();
                _panelSlot.Dispose();
            }
            catch (Exception)
            {
                // A view that is closing.
            }
        }

        private void OnClosed(object sender, EventArgs e) => Dispose();
        private void OnChanged(object sender, EventArgs e) => Update();
        private void OnLoaded(object sender, RoutedEventArgs e) => Update();
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();
        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e) => _view.QueuePostLayoutAction(Update);
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e) => Update();

        public void Update()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed) { return; }
            if (_view.IsClosed || _controller.Exited || !_view.VisualElement.IsVisible) { HideAll(); return; }
            Guarded("buttons", UpdateButtons, _buttonsSlot);
            Guarded("panel", UpdatePanel, _panelSlot);
        }

        // One of the two failing does not take the other with it. A failure is a defect, not an editor that is not ready (the text of
        // a view that is being laid out can always be asked), so the first of each is written whatever the log level, and the rest are
        // not repeated: an update comes with every caret move.
        private void Guarded(string what, Action update, AdornerSlot slot)
        {
            try { update(); }
            catch (Exception ex)
            {
                slot.Hide();
                if (_failed.Add(what)) { OutputWindowLogger.Global.LogException("HunkAdornments." + what, ex); }
            }
        }

        private void HideAll()
        {
            _buttonsSlot.Hide();
            _panelSlot.Hide();
        }

        private void UpdateButtons()
        {
            if (!_owner.WantsButtons(_view) || _controller.IsBusy) { _buttonsSlot.Hide(); return; }
            if (!HunkLocator.TryGetHunkAtCaret(_view, out _hunk) || !HunkLocator.TryGetModel(_view, out var model)) { _buttonsSlot.Hide(); return; }
            // Buttons over a hunk that an edit has already moved would act on something else.
            if (!_controller.IsCurrent(_hunk)) { _buttonsSlot.Hide(); return; }
            var difference = HunkLocator.Current(model);
            var anchor = HunkLocator.AnchorIn(_view, model, difference, _hunk);
            if (anchor is null) { _buttonsSlot.Hide(); return; }

            _buttons.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // In the view's own coordinates. Above the hunk's first line when there is room for it, else at the top of the view.
            var top = 0.0;
            var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(anchor.Value);
            if (line is not null)
            {
                var above = line.Top - _view.ViewportTop - _buttons.DesiredSize.Height;
                if (above > 0) { top = above; }
            }
            else if (anchor.Value.Position >= _view.TextViewLines.FormattedSpan.End.Position)
            {
                _buttonsSlot.Hide();
                return;
            }

            if (!_buttonsSlot.Show(Math.Max(0, _view.ViewportWidth - _buttons.DesiredSize.Width), top)) { NoLayer(); }
        }

        private void UpdatePanel()
        {
            // Before the editor has worked out the first difference there is nothing to say: "No changes" would be untrue.
            if (!_owner.WantsPanel(_view) || _controller.Difference is null) { _panelSlot.Hide(); return; }
            var (change, changes) = _controller.ChangePosition(_view);
            var (file, files) = _controller.FilePosition();
            _panel.Set(change, changes, file, files, _controller.IsBusy);
            _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (!_panelSlot.Show(Math.Max(0, (_view.ViewportWidth - _panel.DesiredSize.Width) / 2),
                                 Math.Max(0, _view.ViewportHeight - _panel.DesiredSize.Height - PanelMargin)))
            {
                NoLayer();
            }
        }

        // The view is shown and has no adorner layer to draw on. Said once: the person is looking at a review with no buttons in
        // it, and the log is where they will look for the reason.
        private void NoLayer()
        {
            if (_noLayerLogged) { return; }
            _noLayerLogged = true;
            OutputWindowLogger.Global.Info("[changes] a review view is shown but has no adorner layer: its buttons and its panel cannot be drawn (the context menu has the same commands)");
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

        private static void Run(Func<System.Threading.Tasks.Task> action)
            => ThreadHelper.JoinableTaskFactory.RunAsync(async () => { await action(); }).FileAndForget("HunkAdornments.Panel");
    }

    /// <summary>One element in the adorner layer of a view, at a point of the view. Puts the adorner back every time it is shown and
    /// finds it gone: the layer removes the adorners of an element that has left the visual tree (a tab that is not shown) and never
    /// returns them. The element is hidden, not collapsed: a collapsed element measures to nothing, and where the buttons and the panel
    /// go is worked out from what they measure.</summary>
    private sealed class AdornerSlot : IDisposable
    {
        private readonly IWpfTextView _view;
        private readonly FrameworkElement _content;
        private AdornerLayer _layer;
        private ContentAdorner _adorner;

        public AdornerSlot(IWpfTextView view, FrameworkElement content)
        {
            _view = view;
            _content = content;
            _content.Visibility = Visibility.Hidden;
        }

        /// <summary>False when the view is in no tree that has an adorner layer, which is a tab that is not shown.</summary>
        public bool Show(double x, double y)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var layer = AdornerLayer.GetAdornerLayer(_view.VisualElement);
            if (layer is null)
            {
                Detach();
                return false;
            }
            if (_adorner is null || !ReferenceEquals(VisualTreeHelper.GetParent(_adorner), layer))
            {
                if (_adorner is not null)
                {
                    OutputWindowLogger.Global.Debug(() => "[changes] the adorner layer had dropped what a review view draws (its tab was away): put back");
                }
                Detach();
                _adorner = new ContentAdorner(_view.VisualElement, _content);
                layer.Add(_adorner);
                _layer = layer;
            }
            _content.Visibility = Visibility.Visible;
            _adorner.MoveTo(x, y);
            return true;
        }

        public void Hide() => _content.Visibility = Visibility.Hidden;

        public void Dispose() => Detach();

        private void Detach()
        {
            var adorner = _adorner;
            _adorner = null;
            if (adorner is null) { return; }
            try { _layer?.Remove(adorner); }
            catch (Exception) { /* a layer that is going away */ }
            adorner.Detach();
        }
    }

    /// <summary>Hosts an element at a point of the adorned view. Takes up the view, draws nothing of its own and so catches no mouse
    /// input that is not for the element.</summary>
    private sealed class ContentAdorner : Adorner
    {
        private readonly FrameworkElement _content;
        private Point _at;

        public ContentAdorner(UIElement adorned, FrameworkElement content) : base(adorned)
        {
            _content = content;
            AddLogicalChild(content);
            AddVisualChild(content);
        }

        public void MoveTo(double x, double y)
        {
            var at = new Point(x, y);
            if (at == _at) { return; }
            _at = at;
            InvalidateArrange();
        }

        public void Detach()
        {
            RemoveVisualChild(_content);
            RemoveLogicalChild(_content);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _content;

        protected override IEnumerator LogicalChildren
        {
            get { yield return _content; }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _content.Arrange(new Rect(_at, _content.DesiredSize));
            return finalSize;
        }
    }

    /// <summary>The two buttons. Plain WPF in the editor's own theme, not focusable: pressing one must not take the keyboard
    /// from the text, or the buttons would go the moment they are used.</summary>
    private sealed class HunkButtons : Border
    {
        public HunkButtons(Action keep, Action undo)
        {
            Themed.Surface(this);
            Padding = new Thickness(1);
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Themed.Button("Keep", "Keep this change: accept it as it is", keep),
                    Themed.Button("Undo", "Undo this change: put back the text from before Claude's change", undo),
                },
            };
        }
    }

    /// <summary>The floating panel at the bottom of the view: the previous and next change with where the caret is among them, Keep
    /// all and Undo all for the file, and the previous and next file of the list. Not focusable, like the buttons.</summary>
    private sealed class ReviewPanel : Border
    {
        private readonly TextBlock _changes = Themed.Label();
        private readonly TextBlock _files = Themed.Label();
        private readonly FrameworkElement _fileGroup;
        private readonly Button[] _all;

        public ReviewPanel(Action previousChange, Action nextChange, Action keepAll, Action undoAll, Action previousFile, Action nextFile)
        {
            Themed.Surface(this);
            Padding = new Thickness(4, 3, 4, 3);
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Opacity = 0.45 };

            var up = Themed.Button("\u2191", "Previous change", previousChange);
            var down = Themed.Button("\u2193", "Next change", nextChange);
            var keep = Themed.Button("Keep all", "Keep every change in this file: accept it as it is", keepAll);
            var undo = Themed.Button("Undo all", "Undo every change in this file: put back the text from before Claude's changes", undoAll);
            var back = Themed.Button("\u2190", "Previous changed file", previousFile);
            var forward = Themed.Button("\u2192", "Next changed file", nextFile);
            _all = new[] { up, down, keep, undo, back, forward };

            _fileGroup = Row(Themed.Separator(), back, _files, forward);
            Child = Row(up, _changes, down, Themed.Separator(), keep, undo, _fileGroup);
        }

        public void Set(int change, int changes, int file, int files, bool busy)
        {
            _changes.Text = changes == 0 ? "No changes"
                          : change >= 0 ? $"Change {change + 1} of {changes}"
                          : changes == 1 ? "1 change" : $"{changes} changes";
            _files.Text = file >= 0 ? $"File {file + 1} of {files}" : $"{files} files";
            // The other files are somewhere to go only when there are some.
            _fileGroup.Visibility = files > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var button in _all) { button.IsEnabled = !busy; }
        }

        private static StackPanel Row(params UIElement[] children)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var child in children) { row.Children.Add(child); }
            return row;
        }
    }

    /// <summary>What the buttons and the panel are made of, in the editor's own theme.</summary>
    private static class Themed
    {
        public static void Surface(Border border)
        {
            border.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            border.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(3);
        }

        public static Button Button(string text, string tip, Action click)
        {
            var button = new Button
            {
                Content = text,
                ToolTip = tip,
                Focusable = false,
                Margin = new Thickness(1),
                Padding = new Thickness(8, 1, 8, 1),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                Template = ButtonTemplate(),
            };
            button.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            button.SetResourceReference(Control.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Click += (_, _) => click();
            return button;
        }

        private static ControlTemplate _buttonTemplate;

        // A flat button that takes its colours from the theme. The stock button has a light face of its own, which the theme's light
        // text is lost on in a dark theme.
        private static ControlTemplate ButtonTemplate()
        {
            if (_buttonTemplate is not null) { return _buttonTemplate; }

            var face = new FrameworkElementFactory(typeof(Border), "Face");
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            face.SetBinding(Border.BackgroundProperty, FromTemplatedParent(Control.BackgroundProperty));
            face.SetBinding(Border.BorderBrushProperty, FromTemplatedParent(Control.BorderBrushProperty));
            face.SetBinding(Border.BorderThicknessProperty, FromTemplatedParent(Control.BorderThicknessProperty));
            face.SetBinding(Border.PaddingProperty, FromTemplatedParent(Control.PaddingProperty));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            face.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = face };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowButtonHoverActiveBrushKey), "Face"));
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(EnvironmentColors.ToolWindowButtonDownBrushKey), "Face"));
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(hover);
            template.Triggers.Add(pressed);
            template.Triggers.Add(off);
            template.Seal();
            _buttonTemplate = template;
            return template;
        }

        private static Binding FromTemplatedParent(DependencyProperty property)
            => new() { RelativeSource = RelativeSource.TemplatedParent, Path = new PropertyPath(property) };

        public static TextBlock Label()
        {
            var label = new TextBlock { Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return label;
        }

        public static FrameworkElement Separator()
        {
            var line = new Border { Width = 1, Margin = new Thickness(4, 3, 4, 3) };
            line.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            return line;
        }
    }
}

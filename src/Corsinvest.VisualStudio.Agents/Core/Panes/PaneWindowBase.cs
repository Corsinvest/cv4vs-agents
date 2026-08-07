/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>
/// Shared base for the multi-instance session pane windows (chat / cli). Holds
/// the common VS plumbing (id assignment, restore fallback, close) so concrete
/// windows just declare their <c>[Guid]</c> (distinct per kind), caption prefix,
/// and hosted control.
/// </summary>
public abstract class PaneWindowBase : ToolWindowPane
{
    /// <summary>The hosted control (chat WebView / cli terminal); registers in the registry via its IPaneControl.RegisterInstance.</summary>
    protected PaneControlBase PaneControl { get; private set; }

    /// <summary>The shared toolbar mounted above <see cref="PaneControl"/>.</summary>
    private PaneToolbar _toolbar;

    protected PaneWindowBase() : base(null)
    {
        OutputWindowLogger.Global.Perf(() => $"{GetType().Name}: ctor begin");
        try
        {
            // Toolbar on top, control filling the rest; toolbar drives the pane
            // via IPaneControl so it stays kind-agnostic.
            PaneControl = CreateControl();
            var dock = new DockPanel { LastChildFill = true };

            // Above the toolbar so it reads as a property of the build rather than of the pane,
            // and here rather than in either control so both kinds get it from one place.
            var preview = BuildPreviewBanner();
            if (preview != null)
            {
                DockPanel.SetDock(preview, Dock.Top);
                dock.Children.Add(preview);
            }

            _toolbar = new PaneToolbar();
            DockPanel.SetDock(_toolbar, Dock.Top);
            dock.Children.Add(_toolbar);
            dock.Children.Add(PaneControl);
            _toolbar.Attach(PaneControl);
            Content = dock;

            OutputWindowLogger.Global.Perf(() => $"{GetType().Name}: ctor done");
        }
        catch (System.Exception ex)
        {
            OutputWindowLogger.Global.LogException($"{GetType().Name}.ctor", ex);
            throw;
        }
    }

    /// <summary>
    /// A red strip naming the preview, or null on a stable build. Fixed colours rather than VS
    /// theme brushes: the point is to stand out against the pane whatever the theme is doing.
    /// </summary>
    private static UIElement BuildPreviewBanner()
    {
        if (!BuildInfo.IsPreRelease) { return null; }

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xA8, 0x00, 0x00)),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = $"Pre-release {BuildInfo.Version} — not for production use",
                Foreground = Brushes.White,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"This build of {AppConstants.AppName} is a preview. "
                        + "Install a stable release from the Marketplace when one is available.",
            },
        };
    }

    /// <summary>Build the hosted control (ChatPaneControl / CliPaneControl).</summary>
    protected abstract PaneControlBase CreateControl();

    /// <summary>True when this pane's frame is VS's active window frame (the user is looking at it).
    /// VS hands out distinct COM proxies for the same window, so ReferenceEquals is unreliable —
    /// compare the hosted DocView object instead.</summary>
    internal bool IsActiveFrame()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Frame is not IVsWindowFrame frame) { return false; }
        if (Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is not IVsMonitorSelection sel) { return false; }
        if (sel.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out var active) != VSConstants.S_OK
            || active is not IVsWindowFrame activeFrame)
        {
            return false;
        }
        var mine = FrameDocView(frame);
        return mine != null && ReferenceEquals(mine, FrameDocView(activeFrame));
    }

    private static object FrameDocView(IVsWindowFrame frame)
        => frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var view) == VSConstants.S_OK ? view : null;

    /// <summary>Multi-instance id assigned by VS (0, 1, 2, …). Set via
    /// <see cref="AssignPaneId"/> right after FindToolWindow, because
    /// VSFPROPID_MultiInstanceToolNum isn't always populated by the time
    /// <see cref="OnToolWindowCreated"/> runs.</summary>
    public int PaneId { get; private set; }

    private bool _assigned;

    /// <summary>Called right after pane creation: set the id, refresh the
    /// caption, and trigger the control's registry attach. Idempotent.</summary>
    internal void AssignPaneId(int paneId)
    {
        if (PaneId == paneId && _assigned) { return; }
        PaneId = paneId;
        _assigned = true;
        OutputWindowLogger.Global.Perf(() => $"{GetType().Name}: AssignPaneId={PaneId}");
        PaneControl.RegisterInstance(this);
    }

    /// <summary>Inject the entry into the hosted control, then activate the in-IDE diff for its
    /// config-dir. Called by PaneLauncher right after creation, before the pane loads.</summary>
    internal void Init(PaneEntry entry)
    {
        PaneControl.Init(entry);
        // The CLI gates the in-IDE diff on diffTool==='auto' but does not write that key for our
        // entrypoint; ensure it for this pane's config-dir so our diff works. Idempotent.
        AgentsPackage.EnsureDiffToolAuto(entry.ClaudePaths.SettingsFile);
    }

    /// <summary>Set the pane caption from the entry's single-source <see cref="PaneEntry.Title"/>
    /// (e.g. "Chat 3 (z.ai)"), the same text the toolbar's open-panes list shows.</summary>
    internal void SetSessionCaption(PaneEntry entry)
    {
        // Sets the pane title only. The docked TAB caption can't be changed here:
        // VS derives it from the window name + instance number and ignores every
        // frame caption property (Caption/ShortCaption/Owner/Editor/OverrideCaption).
        Caption = entry.Title;
    }

    public override void OnToolWindowCreated()
    {
        base.OnToolWindowCreated();
        // Restore fallback: persisted panes never hit the explicit OpenNew path,
        // so read the multi-instance index from the frame if not already assigned.
        // Gate on Entry: AssignPaneId → RegisterInstance dereferences the entry, and this
        // callback can fire (inside FindToolWindow(create:true)) BEFORE PaneLauncher.Init has
        // injected it. When it runs early, PaneLauncher's own AssignPaneId call registers later.
        if (!_assigned
            && PaneControl.Entry != null
            && Frame is IVsWindowFrame frame
            && frame.GetProperty((int)__VSFPROPID.VSFPROPID_MultiInstanceToolNum, out var raw) == VSConstants.S_OK
            && raw is int n)
        {
            AssignPaneId(n);
        }

        HookFrameShow();
    }

    /// <summary>Focus the input when the frame is shown. Clicking a docked pane's TAB goes through
    /// neither path that already focuses — ActivatePane (InfoBar, toast, the toolbar's pane list)
    /// and the toolbar buttons — so focus landed on the first focusable child, the More button, and
    /// Space opened a menu instead of typing.
    ///
    /// VSFPROPID_ViewHelper is the frame's single notify slot: read what is already there and
    /// forward to it, so this doesn't silently displace another listener.</summary>
    private void HookFrameShow()
    {
        if (Frame is not IVsWindowFrame frame) { return; }
        try
        {
            frame.GetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, out var existing);
            frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper,
                              new FrameShowListener(this, existing as IVsWindowFrameNotify3));
        }
        catch (System.Exception ex)
        {
            // Focus-on-tab-click is a convenience: losing it must not stop the pane from opening.
            OutputWindowLogger.Global.LogException("[panes] HookFrameShow", ex);
        }
    }

    /// <summary>Frame notify that focuses this pane's input when it is shown. Chains to whatever
    /// listener held the slot before, since a frame has only one.</summary>
    private sealed class FrameShowListener(PaneWindowBase owner, IVsWindowFrameNotify3 next) : IVsWindowFrameNotify3
    {
        public int OnShow(int fShow)
        {
            if (fShow is (int)__FRAMESHOW.FRAMESHOW_WinShown or (int)__FRAMESHOW.FRAMESHOW_TabActivated)
            {
                // Deferred like ActivatePane's: OnShow runs mid-activation, so focusing inline is
                // overwritten by the shell a moment later. The IsActiveFrame gate is evaluated in
                // the same continuation — asking during OnShow can still name the previous frame.
                owner.FocusInputIfActive();
            }
            return next?.OnShow(fShow) ?? VSConstants.S_OK;
        }

        public int OnClose(ref uint pgrfSaveOptions)
            => next == null ? VSConstants.S_OK : next.OnClose(ref pgrfSaveOptions);

        public int OnMove(int x, int y, int w, int h) => next?.OnMove(x, y, w, h) ?? VSConstants.S_OK;
        public int OnSize(int x, int y, int w, int h) => next?.OnSize(x, y, w, h) ?? VSConstants.S_OK;
        public int OnDockableChange(int fDockable, int x, int y, int w, int h)
            => next?.OnDockableChange(fDockable, x, y, w, h) ?? VSConstants.S_OK;
    }

    /// <summary>Focus the input, but only if this pane is the one the user is actually looking at.
    /// OnShow also fires when a pane merely becomes visible (a dock group opening, another tab
    /// closing); focusing then would pull the caret out of whatever the user was typing in.</summary>
    private void FocusInputIfActive()
    {
        _ = Application.Current?.Dispatcher.BeginInvoke(
            new System.Action(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try
                {
                    if (IsActiveFrame()) { PaneControl.FocusInput(); }
                }
                catch (System.ObjectDisposedException) { }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    OutputWindowLogger.Global.LogException("[panes] FocusInputIfActive", ex);
                }
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Bring THIS pane to the front and focus its input. Keyed off the
    /// live pane instance, not a PaneId (VS recycles ids, so id-based lookup can
    /// resolve the wrong pane). Focus goes through IPaneControl for both kinds.</summary>
    internal void ActivatePane()
    {
        // The InfoBar / OS toast that triggers this can outlive the pane: the user may
        // close the pane, then click "Go to". The Frame reference survives but its underlying
        // MarshalingWindowFrame is disposed, so Show()/FocusInput() throw. Nothing to activate.
        try
        {
            if (Frame is IVsWindowFrame frame)
            {
                ErrorHandler.ThrowOnFailure(frame.Show());
            }
        }
        catch (System.ObjectDisposedException)
        {
            OutputWindowLogger.Global.Warn("[panes] ActivatePane skipped: the pane was already closed");
            return;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OutputWindowLogger.Global.LogException("[panes] ActivatePane", ex);
            return;
        }

        FocusInputWhenShellSettles();
    }

    /// <summary>Focus the pane's input once the shell has finished activating the frame.
    ///
    /// The delay is the whole point. Focusing inline works on a pane that is already visible —
    /// Show() does no real work there — which is why this defect only ever showed on a HIDDEN pane:
    /// there the activation is real, and VS gives focus to the frame's first focusable child (the
    /// toolbar's More button) when it completes, overwriting ours. Yielding to Background puts our
    /// focus last. The same applies to any other caller that focuses around an activation.
    ///
    /// Application.Current.Dispatcher, not this.Dispatcher: ToolWindowPane isn't a
    /// DispatcherObject. Same UI thread either way.</summary>
    private void FocusInputWhenShellSettles()
    {
        _ = Application.Current?.Dispatcher.BeginInvoke(
            new System.Action(() =>
            {
                try
                {
                    PaneControl.FocusInput();
                }
                catch (System.ObjectDisposedException)
                {
                    OutputWindowLogger.Global.Warn("[panes] focus skipped: the pane was closed meanwhile");
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    OutputWindowLogger.Global.LogException("[panes] FocusInputWhenShellSettles", ex);
                }
            }),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Wire a freshly-created instance entry to this pane: set the
    /// close/activate callbacks, add it to the registry, push the caption.
    /// Common attach boilerplate kept here so both kinds share one path.</summary>
    internal void RegisterInstance(PaneEntry entry)
    {
        entry.CloseAction = ClosePane;
        entry.ActivateAction = ActivatePane;
        entry.ShowHistoryAction = () => _toolbar?.ShowSessionHistory();
        entry.DismissHistoryAction = () => _toolbar?.DismissSessionHistory() == true;
        PaneRegistry.Instance.Add(entry);
        SetSessionCaption(entry);
        OutputWindowLogger.Global.Info($"{GetType().Name}: attached pane #{PaneId} (seq {entry.SeqNo}), registry now has {PaneRegistry.Instance.Entries.Count} entries");
    }

    /// <summary>Programmatically close this pane (used by the toolbar ✕).</summary>
    internal void ClosePane()
    {
        if (Frame is IVsWindowFrame frame)
        {
            frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
        }
    }

    /// <summary>Single teardown path for ALL pane kinds: on frame dispose (real
    /// close), release the control's resources and drop its registry entry. Kept
    /// here, not per window — Chat once tore down on WPF Unloaded, which VS also
    /// fires on hide, dropping a live instance from the "open panes" list.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OutputWindowLogger.Global.Debug(() => $"[pane] frame disposed: {GetType().Name} #{PaneId}");
            try { PaneControl.DisposePane(); }
            catch (System.Exception ex) { OutputWindowLogger.Global.LogException($"{GetType().Name}.Dispose", ex); }
        }
        base.Dispose(disposing);
    }
}

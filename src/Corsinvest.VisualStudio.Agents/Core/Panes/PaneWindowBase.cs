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
        OutputWindowLogger.Perf(() => $"{GetType().Name}: ctor begin");
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

            OutputWindowLogger.Perf(() => $"{GetType().Name}: ctor done");
        }
        catch (System.Exception ex)
        {
            OutputWindowLogger.LogException($"{GetType().Name}.ctor", ex);
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

    /// <summary>Tell VS this pane is the active window. A WebView2 (HwndHost) takes native focus
    /// without updating VS's logical focus, so the shell still thinks the editor is active and
    /// routes keys (Home/End → tab switch) and typing there. Driven by a real in-WebView click
    /// (JS pointerdown → bridge ui_pane_activate), since WPF mouse/focus events can't cross the
    /// HwndHost boundary and GotFocus looped frame.Show() during sibling-tab switches.</summary>
    internal void ActivateFrame()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Frame is IVsWindowFrame frame)
        {
            // Show() activates the frame without changing its dock state — VS updates its
            // "active window" to this pane, so keyboard input now flows to the WebView.
            frame.Show();
        }
    }

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
        OutputWindowLogger.Perf(() => $"{GetType().Name}: AssignPaneId={PaneId}");
        PaneControl.RegisterInstance(this);
        // Blur our input when VS moves the active frame elsewhere (below). AssignPaneId is
        // idempotent (early-returns when already assigned), so this subscribes once.
        Ide.IdeContextService.ActiveFrameChanged += OnActiveFrameChanged;
    }

    /// <summary>When VS's active frame changes and this pane is no longer it, blur its input:
    /// the WebView2 gets no DOM blur across the HwndHost boundary, so its caret would keep
    /// blinking. Gate strictly on IsActiveFrame so the newly-active pane keeps its caret.</summary>
    private void OnActiveFrameChanged()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsActiveFrame()) { PaneControl.BlurInput(); }
    }

    /// <summary>Inject the entry into the hosted control, then activate the in-IDE diff for its
    /// config-dir. Called by PaneLauncher right after creation, before the pane loads.</summary>
    internal void Init(PaneEntry entry)
    {
        PaneControl.Init(entry);
        // The CLI gates the in-IDE diff on diffTool==='auto' but only writes it for VS Code;
        // ensure the key for this pane's config-dir so our diff works. Idempotent.
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
            PaneControl.FocusInput();
        }
        catch (System.ObjectDisposedException)
        {
            OutputWindowLogger.Warn("[panes] ActivatePane skipped: the pane was already closed");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            OutputWindowLogger.LogException("[panes] ActivatePane", ex);
        }
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
        OutputWindowLogger.Info($"{GetType().Name}: attached pane #{PaneId} (seq {entry.SeqNo}), registry now has {PaneRegistry.Instance.Entries.Count} entries");
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
            Ide.IdeContextService.ActiveFrameChanged -= OnActiveFrameChanged;
            try { PaneControl.DisposePane(); }
            catch (System.Exception ex) { OutputWindowLogger.LogException($"{GetType().Name}.Dispose", ex); }
        }
        base.Dispose(disposing);
    }
}

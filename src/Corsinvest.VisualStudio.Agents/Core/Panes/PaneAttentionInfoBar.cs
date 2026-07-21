/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>A pane-attention InfoBar on the VS main window: "&lt;pane&gt; needs your input" /
/// "&lt;pane&gt; finished", with a "Go to pane" action that activates the owning frame. Hosted on
/// the main window (not the pane frame) so it's visible even when the pane is a hidden tab.</summary>
internal sealed class PaneAttentionInfoBar : IVsInfoBarUIEvents
{
    private readonly Action _onGoTo;
    private IVsInfoBarUIElement _element;
    private uint _cookie;
    private readonly object _goTo = new();
    private Action _onClosedExternal;

    private PaneAttentionInfoBar(Action onGoTo) => _onGoTo = onGoTo;

    /// <summary>Create + attach an InfoBar to the main window's InfoBar host. Returns null (no-op)
    /// if the host/factory isn't available. <paramref name="onGoTo"/> activates the pane;
    /// <paramref name="onClosed"/> lets the caller drop its reference when the bar closes.</summary>
    public static PaneAttentionInfoBar TryShow(string message, bool isError, Action onGoTo, Action onClosed)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!TryGetMainWindowInfoBarHost(out var host)) { return null; }
            if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) { return null; }

            var bar = new PaneAttentionInfoBar(onGoTo) { _onClosedExternal = onClosed };
            var model = new InfoBarModel(
                textSpans: [new InfoBarTextSpan(message + " ")],
                actionItems: [new InfoBarButton("Go to pane", bar._goTo)],
                image: isError ? KnownMonikers.StatusWarning : KnownMonikers.StatusInformation,
                isCloseButtonVisible: true);

            bar._element = factory.CreateInfoBar(model);
            bar._element.Advise(bar, out bar._cookie);
            host.AddInfoBar(bar._element);
            return bar;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("PaneAttentionInfoBar.TryShow", ex);
            return null;
        }
    }

    public void Close()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _element?.Close(); } catch { /* best effort */ }
    }

    private static bool TryGetMainWindowInfoBarHost(out IVsInfoBarHost host)
    {
        host = null;
        if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) { return false; }
        if (shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObj) != VSConstants.S_OK) { return false; }
        host = hostObj as IVsInfoBarHost;
        return host != null;
    }

    void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(actionItem.ActionContext, _goTo)) { _onGoTo?.Invoke(); }
        try { infoBarUIElement.Close(); } catch { /* best effort */ }
    }

    void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { infoBarUIElement.Unadvise(_cookie); } catch { /* best effort */ }
        _onClosedExternal?.Invoke();
    }
}

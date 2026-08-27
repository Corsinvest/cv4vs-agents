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

/// <summary>A debugger-break InfoBar: "Paused on &lt;exception&gt; at &lt;file&gt;:&lt;line&gt;" with an
/// "Ask cv4vs Agents" action. Hosted on the window frame of the file the break happened in — the user is
/// looking at that editor, not at the top of the shell — and falls back to the main window when
/// that document isn't open. <see cref="DebugBreakService"/> decides when one is worth raising.</summary>
internal sealed class DebugBreakInfoBar : IVsInfoBarUIEvents
{
    private readonly Action _onAsk;
    private readonly object _ask = new();
    private IVsInfoBarUIElement _element;
    private uint _cookie;
    private Action _onClosedExternal;

    private DebugBreakInfoBar(Action onAsk) => _onAsk = onAsk;

    /// <summary>Create + attach the bar. Returns null (no-op) when no InfoBar host or factory is
    /// available. <paramref name="filePath"/> selects the document frame to host it on;
    /// <paramref name="onAsk"/> runs on the ask click, <paramref name="onClosed"/> lets the
    /// caller drop its reference once the bar goes away.</summary>
    public static DebugBreakInfoBar TryShow(string message, string filePath, Action onAsk, Action onClosed)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (!TryGetInfoBarHost(filePath, out var host)) { return null; }
            if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) { return null; }

            var bar = new DebugBreakInfoBar(onAsk) { _onClosedExternal = onClosed };
            var model = new InfoBarModel(
                textSpans: [new InfoBarTextSpan(message + " ")],
                actionItems: [new InfoBarButton($"Ask {AppConstants.AppName}", bar._ask)],
                image: KnownMonikers.StatusWarning,
                isCloseButtonVisible: true);

            bar._element = factory.CreateInfoBar(model);
            bar._element.Advise(bar, out bar._cookie);
            host.AddInfoBar(bar._element);
            return bar;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("DebugBreakInfoBar.TryShow", ex);
            return null;
        }
    }

    public void Close()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _element?.Close(); } catch { /* best effort */ }
    }

    /// <summary>The InfoBar host of the frame showing <paramref name="filePath"/>, or the main
    /// window's when the file isn't open. Deliberately does not open the document: a break can land
    /// in a file the user never asked to see, and opening tabs mid-debug moves ground under them.</summary>
    private static bool TryGetInfoBarHost(string filePath, out IVsInfoBarHost host)
    {
        host = null;
        if (!string.IsNullOrEmpty(filePath)
            && VsShellUtilities.IsDocumentOpen(
                ServiceProvider.GlobalProvider, filePath, Guid.Empty, out _, out _, out var frame)
            && frame?.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var frameHost) == VSConstants.S_OK)
        {
            host = frameHost as IVsInfoBarHost;
            if (host != null) { return true; }
        }

        if (Package.GetGlobalService(typeof(SVsShell)) is not IVsShell shell) { return false; }
        if (shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out var hostObj) != VSConstants.S_OK) { return false; }
        host = hostObj as IVsInfoBarHost;
        return host != null;
    }

    void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ReferenceEquals(actionItem.ActionContext, _ask)) { _onAsk?.Invoke(); }
        try { infoBarUIElement.Close(); } catch { /* best effort */ }
    }

    void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { infoBarUIElement.Unadvise(_cookie); } catch { /* best effort */ }
        _onClosedExternal?.Invoke();
    }
}

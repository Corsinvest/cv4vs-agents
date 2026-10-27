/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Pane;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The WebView2 internals the chat pane reaches by reflection.
/// <para>ChatWebView follows a pane into a floating window through
/// <c>WebView2CompositionControl.m_webview2Base.CoreWebView2Controller</c>, neither of which is public.
/// If an SDK bump renames either, the lookup comes back empty and the fix switches itself off: nothing
/// fails, a floating pane just stops taking typing. This makes that bump a red build.</para>
/// <para>It checks the copy this project references. Visual Studio loads its own at runtime, which
/// no test reaches: there the <c>[webview]</c> line in the Output window is the safety net.</para></summary>
public class ChatWebViewTests
{
    [Fact]
    public void WebView2_internals_the_floating_pane_fix_reaches_are_still_there()
        => Assert.True(
            ChatWebView.InternalsFound,
            "WebView2CompositionControl no longer has m_webview2Base.CoreWebView2Controller, or it no "
            + "longer returns a CoreWebView2Controller. ChatWebView needs it to re-point the browser's "
            + "parent window when a pane floats: update the lookup there, or a floating chat pane "
            + "stops taking typing.");
}

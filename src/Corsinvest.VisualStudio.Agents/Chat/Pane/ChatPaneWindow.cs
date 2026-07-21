/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using System;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Chat.Pane;

/// <summary>
/// Pane hosting a single Claude chat session in an embedded WebView2 UI (multi-instance).
/// Id/lifecycle plumbing lives in <see cref="PaneWindowBase"/>; this subclass only sets
/// the GUID (so VS numbers chat panes separately), caption, and hosted control.
/// </summary>
[Guid("e4f5a6b7-c8d9-0123-efab-de3456789012")]
public sealed class ChatPaneWindow : PaneWindowBase, IOleCommandTarget
{
    protected override PaneControlBase CreateControl() => new ChatPaneControl();

    // VS turns keystrokes into commands and routes them down the IOleCommandTarget chain (the
    // active pane is asked first). We claim two and hand them to the chat's WebView instead of
    // letting VS act on them; everything else falls through unchanged (F5/build/Ctrl+S stay VS):
    //  - Find (Ctrl+F): VSStd97 Find → open the WebView2 find bar, not VS's Find dialog.
    //  - Esc: VSStd97 cmdID 289 — claim it so VS doesn't move focus to an open editor;
    //    forward to the WebView (stop generation / close a menu). 289 verified by debugging
    //    QueryStatus; the named enum value didn't match, so pin the literal.
    private static readonly Guid VsStd97 = VSConstants.GUID_VSStandardCommandSet97;
    private const uint CmdidFind = (uint)VSConstants.VSStd97CmdID.Find;
    private const uint CmdidCancel = 289;

    int IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        if (cCmds == 1 && pguidCmdGroup == VsStd97
            && (prgCmds[0].cmdID == CmdidFind || prgCmds[0].cmdID == CmdidCancel))
        {
            prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            return VSConstants.S_OK;
        }
        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    int IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        if (pguidCmdGroup == VsStd97 && PaneControl is ChatPaneControl ctl)
        {
            if (nCmdID == CmdidFind && ctl.ShowFind()) { return VSConstants.S_OK; }
            if (nCmdID == CmdidCancel && ctl.HandleEscape()) { return VSConstants.S_OK; }
        }
        return (int)Constants.OLECMDERR_E_NOTSUPPORTED;
    }

    /// <summary>Forward to the hosted control so the launcher can seed a forked
    /// session before the pane loads (Content is a DockPanel, not the control).</summary>
    internal void SetStartupSession(string sessionId, string initialPrompt = null)
    {
        if (PaneControl is ChatPaneControl ctl) { ctl.SetStartupSession(sessionId, initialPrompt); }
    }

}

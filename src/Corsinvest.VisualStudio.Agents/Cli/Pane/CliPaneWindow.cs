/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Corsinvest.VisualStudio.Agents.Cli.Pane;

/// <summary>
/// Pane hosting a single live <c>claude --ide</c> session in an embedded
/// terminal (ConPTY + Microsoft.Terminal.Wpf). Multi-instance: each "New CLI"
/// gives a fresh pane. Lifecycle plumbing lives in <see cref="PaneWindowBase"/>;
/// this subclass declares only the GUID, caption, hosted control, and the
/// terminal key routing below (Esc + navigation keys forwarded to claude).
/// </summary>
[Guid("f5a6b7c8-d9e0-1234-fabc-ef4567890123")]
public sealed class CliPaneWindow : PaneWindowBase
{
    // Kitty-keyboard-protocol escape sequence — same one VS's own
    // TerminalWindowBase.EscKeyCode; keeps Esc routed to the CLI instead of
    // "close tool window".
    private const string EscKeySequence = "[27;1;27;1;0;1_";

    protected override PaneControlBase CreateControl() => new CliPaneControl();

    /// <summary>Forward to the hosted control so the launcher can point the terminal at a session
    /// before it starts (workspace restore). Content is a DockPanel, not the control — go through the
    /// protected PaneControl (LoadSession is on IPaneControl, so no cast needed).</summary>
    internal void LoadSession(string sessionId) => PaneControl.LoadSession(sessionId);

    protected override bool PreProcessMessage(ref Message m)
    {
        const int WM_KEYDOWN = 0x0100;
        if (m.Msg == WM_KEYDOWN)
        {
            var key = (int)m.WParam & 0xFF;
            var mods = Control.ModifierKeys;
            var ctl = PaneControl as CliPaneControl;

            // Esc (any modifiers): forward as Kitty sequence and consume so VS
            // doesn't deactivate the pane.
            if (key == 27)
            {
                ctl?.SendInput(EscKeySequence);
                return true;
            }

            // Ctrl+<letter>: forward as raw control byte (A=0x01 …) and consume,
            // else VS commanding would steal Ctrl+C/V/R/B/O/L from claude.
            if (mods == Keys.Control && key >= (int)Keys.A && key <= (int)Keys.Z)
            {
                var ctrlByte = (char)(key - (int)Keys.A + 1);
                ctl?.SendInput(ctrlByte.ToString());
                return true;
            }

            // Navigation/edit keys (PgUp..arrows 33-40, Backspace 8, Tab 9,
            // Enter 13, Delete 46): let normal Win32 dispatch reach TerminalControl.
            if (key is >= 33 and <= 40 or 8 or 9 or 13 or 46) { return false; }
        }
        return base.PreProcessMessage(ref m);
    }
}

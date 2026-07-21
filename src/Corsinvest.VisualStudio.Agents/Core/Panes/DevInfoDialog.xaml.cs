/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.PlatformUI;
using System.Windows;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>Read-only session-info dump from a pane's More menu, for debug/support (session file,
/// session id, CLI path/version). Shared by both pane kinds. Inherits <see cref="DialogWindow"/>
/// so chrome follows the active VS theme; the caller supplies the formatted text.</summary>
public partial class DevInfoDialog : DialogWindow
{
    public DevInfoDialog(string info)
    {
        InitializeComponent();
        TxtInfo.Text = info;
    }

    private void OnCopy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TxtInfo.Text);
        TxtInfo.SelectAll();
    }
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.PlatformUI;
using System;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;

namespace Corsinvest.VisualStudio.Agents.Menu;

/// <summary>About dialog from the toolbar More menu. Inherits <see cref="DialogWindow"/>
/// so chrome follows the active VS theme; layout mirrors the WebView's cv-about-dialog.</summary>
public partial class AboutDialog : DialogWindow
{
    public AboutDialog()
    {
        InitializeComponent();
        var asm = typeof(AboutDialog).Assembly;
        TxtVersion.Text = $"v{BuildInfo.Version}";
        TxtCopyright.Text = BuildInfo.Copyright;
        LnkRepo.NavigateUri = new Uri("https://github.com/Corsinvest/cv4vs-agents");
        LnkCorsinvest.NavigateUri = new Uri("https://www.corsinvest.it");
    }

    private void OnLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.NavigateUri != null)
        {
            Helpers.ShellHelpers.OpenExternal(link.NavigateUri.ToString());
        }
    }

    private void OnClose_Click(object sender, RoutedEventArgs e) => Close();
}

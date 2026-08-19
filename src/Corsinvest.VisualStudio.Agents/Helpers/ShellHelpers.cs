/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Diagnostics;

namespace Corsinvest.VisualStudio.Agents.Helpers;

internal static class ShellHelpers
{
    /// <summary>Open a URL, file or folder with the OS default handler. Always sets
    /// UseShellExecute=true (required to launch a URL/document; the .NET default is
    /// false, which would throw). Best-effort: swallows failures so a dead link
    /// never crashes the caller.</summary>
    public static void OpenExternal(string target)
    {
        if (string.IsNullOrEmpty(target)) { return; }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("ShellHelpers.OpenExternal", ex);
        }
    }

    /// <summary>A themed message box, in place of System.Windows.MessageBox — WPF's own dialog is
    /// drawn by Windows and knows nothing about the IDE, so on a dark theme it opens white.
    /// <para>Not Community.VisualStudio.Toolkit's VS.MessageBox: it makes the same
    /// VsShellUtilities call underneath, but its ShowWarning shows OK/Cancel rather than OK, and
    /// its ShowConfirm hardcodes Yes as the default button (see <see cref="Confirm"/>).</para>
    /// <para>UI thread only — the shell cannot raise a dialog from anywhere else.</para></summary>
    public static void ShowMessage(string text, string title, bool warning = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            text,
            title,
            warning ? OLEMSGICON.OLEMSGICON_WARNING : OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>A themed Yes/No prompt; true when the user picked Yes.
    /// <para>Defaults to No, so a stray Enter or Esc cancels rather than confirms — every caller
    /// guards something irreversible. This is the one reason not to use the toolkit's
    /// ShowConfirm, which defaults to Yes and offers no way to change it.</para></summary>
    public static bool Confirm(string text, string title)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            text,
            title,
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == (int)VSConstants.MessageBoxResult.IDYES;
    }

    /// <summary>A themed OK/Cancel prompt; true when the user picked OK. Same default-to-cancel
    /// reasoning as <see cref="Confirm"/>.</summary>
    public static bool ConfirmOkCancel(string text, string title)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return VsShellUtilities.ShowMessageBox(
            ServiceProvider.GlobalProvider,
            text,
            title,
            OLEMSGICON.OLEMSGICON_WARNING,
            OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND) == (int)VSConstants.MessageBoxResult.IDOK;
    }
}

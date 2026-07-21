/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

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
            OutputWindowLogger.LogException("ShellHelpers.OpenExternal", ex);
        }
    }
}

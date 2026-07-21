/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Corsinvest.VisualStudio.Agents.Chat;

internal static class VsThemeReader
{
    public static bool IsDark()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell5;
            var bg = shell.GetThemedWPFColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            return ColorUtils.IsDark(bg.R, bg.G, bg.B);
        }
        catch { return true; }
    }
}

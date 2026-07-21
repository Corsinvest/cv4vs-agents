/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeDebugService shared helpers used by both the control/breakpoint side and the
/// live-inspection side: DTE/Debugger access, mode formatting, the paused location,
/// and value cleanup.
/// </summary>
internal sealed partial class IdeDebugService
{
    private static DTE GetDte() => Package.GetGlobalService(typeof(DTE)) as DTE;
    private static Debugger GetDebugger() => GetDte()?.Debugger;

    private static string ModeToString(dbgDebugMode mode) => mode switch
    {
        dbgDebugMode.dbgDesignMode => "design",
        dbgDebugMode.dbgRunMode => "run",
        dbgDebugMode.dbgBreakMode => "break",
        _ => "unknown",
    };

    /// <summary>Current file + 1-based line VS shows while paused (from the active document).</summary>
    private static (string file, int line) CurrentLocation()
    {
        try
        {
            var doc = GetDte()?.ActiveDocument;
            var file = doc?.FullName;
            var line = doc?.Selection is TextSelection sel ? sel.ActivePoint.Line : 0;
            return (file, line);
        }
        catch { return (null, 0); }
    }

    private static string SafeModule(StackFrame sf)
    {
        try { return sf.Module; } catch { return null; }
    }

    /// <summary>The debugger renders string values wrapped in quotes (e.g. "boom"); strip a single
    /// surrounding pair so the model gets the bare message.</summary>
    private static string Unquote(string s)
    {
        return s?.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
    }
}

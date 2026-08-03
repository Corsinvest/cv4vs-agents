/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace Corsinvest.VisualStudio.Agents;

/// <summary>Writes to the extension's pane in the VS Output window.
/// <para>Instances differ only by the tag they prepend: <see cref="For"/> gives a per-session one
/// (<c>[chat#2]</c>) so several open panes can be told apart in what is a single stream, and
/// <see cref="Global"/> writes untagged for everything that belongs to no session — MCP, IDE,
/// package, and any static member with no instance to reach.</para>
/// <para>The pane itself is process-wide, so it stays on the static side: one pane, created once.</para>
/// </summary>
internal sealed class OutputWindowLogger
{
    // A Func, not a string: PaneId is assigned after the control is built
    // (PaneWindowBase.AssignPaneId), so a captured value would tag every start-up line "#0".
    private readonly Func<string> _tag;

    private OutputWindowLogger(Func<string> tag) => _tag = tag;

    /// <summary>Logger for one pane: every line carries <c>[chat#N]</c> / <c>[cli#N]</c>.</summary>
    public static OutputWindowLogger For(string kind, Func<int> paneId) => new(() => $"[{kind}#{paneId()}]");

    /// <summary>Logger for code that belongs to no session. Writes with no tag.</summary>
    public static readonly OutputWindowLogger Global = new(null);

    private string P => _tag == null ? "" : _tag() + " ";

    public void Error(string message) => Write(LogLevel.Error, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Trace(string message) => Write(LogLevel.Trace, message);

    /// <summary>Lazy overload: the factory runs only when the level is on, so callers with an
    /// interpolated message don't pay the string build (and its allocations) when it's off.</summary>
    public void Debug(Func<string> messageFactory)
    {
        if (LogLevel.Debug > AgentsOptions.Debug.LogLevel) { return; }
        Write(LogLevel.Debug, messageFactory());
    }

    public void Trace(Func<string> messageFactory)
    {
        if (LogLevel.Trace > AgentsOptions.Debug.LogLevel) { return; }
        Write(LogLevel.Trace, messageFactory());
    }

    public void Perf(string message)
    {
        if (!AgentsOptions.Debug.EnablePerfLog) { return; }
        WriteLine($"PERF {P}{message}");
    }

    /// <summary>Lazy overload: same reason as <see cref="Debug(Func{string})"/>.</summary>
    public void Perf(Func<string> messageFactory)
    {
        if (!AgentsOptions.Debug.EnablePerfLog) { return; }
        WriteLine($"PERF {P}{messageFactory()}");
    }

    public IDisposable PerfSpan(string label) =>
        AgentsOptions.Debug.EnablePerfLog ? new PerfTimer(this, label) : NullDisposable.Instance;

    private sealed class PerfTimer(OutputWindowLogger owner, string label) : IDisposable
    {
        private readonly DateTime _t0 = DateTime.Now;
        public void Dispose() => owner.Perf($"{label} {(DateTime.Now - _t0).TotalMilliseconds:0}ms");
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    // Always logs (bypasses LogLevel) — losing exceptions silently would
    // make the extension impossible to diagnose in production.
    public void LogException(string context, Exception ex)
    {
        if (ex == null) { return; }
        var ctx = P + (context ?? "");
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " !!! "
                 + (string.IsNullOrEmpty(ctx) ? "" : ctx + ": ")
                 + ex.GetType().Name + ": " + ex.Message
                 + Environment.NewLine + ex.StackTrace;
        // The [AppId] prefix only matters in the debugger's Output stream, where our lines mix
        // with everything else; our own pane is already dedicated to the extension.
        System.Diagnostics.Debug.WriteLine("[" + AppConstants.AppId + "] " + line);
        _pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }

    private void Write(LogLevel level, string message)
    {
        if (level > AgentsOptions.Debug.LogLevel) { return; }
        WriteLine($"{level.ToString().ToUpperInvariant()} {P}{message}");
    }

    private static void WriteLine(string body)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + body;
        System.Diagnostics.Debug.WriteLine("[" + AppConstants.AppId + "] " + line);
        _pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }

    // The Output window pane is process-wide: one for the whole extension, whoever logs into it.

    private static IVsOutputWindowPane _pane;

    public static void EnsurePaneOnUIThread()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        EnsurePane();
    }

    /// <summary>Bring the Claude Code output pane to front and open the Output window if hidden. Fire-and-forget; switches to the UI thread itself.</summary>
    public static void ActivatePane() => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        EnsurePane();
        _pane?.Activate();
        // Activate only switches the dropdown; also open the Output window in case it's hidden.
        var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        try { dte?.ExecuteCommand("View.Output"); } catch { /* command may not exist in all VS configs */ }
    }).FileAndForget(nameof(OutputWindowLogger));

    private static void EnsurePane()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pane != null) { return; }

        if (Package.GetGlobalService(typeof(SVsOutputWindow)) is not IVsOutputWindow outputWindow) { return; }

        var guid = new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
        // fInitVisible=1, fClearWithSolution=0 — keep logs across solution open/close
        outputWindow.CreatePane(ref guid, AppConstants.AppName, 1, 0);
        outputWindow.GetPane(ref guid, out _pane);
    }
}

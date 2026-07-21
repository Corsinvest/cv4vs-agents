/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace Corsinvest.VisualStudio.Agents;

internal static class OutputWindowLogger
{
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

    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Trace(string message) => Write(LogLevel.Trace, message);

    public static void Perf(string message)
    {
        if (!AgentsOptions.Debug.EnablePerfLog) { return; }
        WriteLine($"PERF {message}");
    }

    /// <summary>Lazy overload: the factory runs only when perf logging is on, so callers with an
    /// interpolated message don't pay the string build (and its allocations) when it's off (default).</summary>
    public static void Perf(Func<string> messageFactory)
    {
        if (!AgentsOptions.Debug.EnablePerfLog) { return; }
        WriteLine($"PERF {messageFactory()}");
    }

    public static IDisposable PerfSpan(string label) =>
        AgentsOptions.Debug.EnablePerfLog ? new PerfTimer(label) : NullDisposable.Instance;

    private sealed class PerfTimer(string label) : IDisposable
    {
        private readonly DateTime _t0 = DateTime.Now;
        public void Dispose() => Perf($"{label} {(DateTime.Now - _t0).TotalMilliseconds:0}ms");
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    public static void Debug(Func<string> messageFactory)
    {
        if (LogLevel.Debug > AgentsOptions.Debug.LogLevel) { return; }
        Write(LogLevel.Debug, messageFactory());
    }

    public static void Trace(Func<string> messageFactory)
    {
        if (LogLevel.Trace > AgentsOptions.Debug.LogLevel) { return; }
        Write(LogLevel.Trace, messageFactory());
    }

    // Always logs (bypasses LogLevel) — losing exceptions silently would
    // make the extension impossible to diagnose in production.
    public static void LogException(string context, Exception ex)
    {
        if (ex == null) { return; }
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " !!! "
                 + (string.IsNullOrEmpty(context) ? "" : context + ": ")
                 + ex.GetType().Name + ": " + ex.Message
                 + Environment.NewLine + ex.StackTrace;
        // The [AppId] prefix only matters in the debugger's Output stream, where our lines mix
        // with everything else; our own pane is already dedicated to the extension.
        System.Diagnostics.Debug.WriteLine("[" + AppConstants.AppId + "] " + line);
        _pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }

    private static void Write(LogLevel level, string message)
    {
        if (level > AgentsOptions.Debug.LogLevel) { return; }
        WriteLine($"{level.ToString().ToUpperInvariant()} {message}");
    }

    private static void WriteLine(string body)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + body;
        System.Diagnostics.Debug.WriteLine("[" + AppConstants.AppId + "] " + line);
        _pane?.OutputStringThreadSafe(line + Environment.NewLine);
    }

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

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// Low-level NDJSON transport: owns the claude.exe process and reads/writes one
/// JSON object per line on stdin/stdout. No protocol logic — caller dispatches lines.
/// </summary>
internal sealed class NdjsonTransport : IDisposable
{
    private Process _process;
    private StreamWriter _stdin;
    // Serializes stdin writes: MCP tool responses are served on a worker thread
    // (HandleMcpMessage) and can race with the UI thread's writes, interleaving
    // two NDJSON lines into one corrupt line. One write+flush at a time.
    private readonly object _writeLock = new();
    private Thread _readerThread;
    private readonly IntPtr _hJob;
    private volatile bool _disposed;

    public event EventHandler<JObject> LineReceived;
    public event EventHandler<string> ErrorLine;
    public event EventHandler<(int exitCode, bool intentional)> Exited;

    /// <summary>Marked when the caller intends to kill the process (e.g. Dispose for respawn).</summary>
    private bool _intentional;

    public int Pid => _process?.Id ?? -1;
    public bool IsRunning => _process?.HasExited == false;

    public NdjsonTransport()
    {
        _hJob = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_hJob != IntPtr.Zero)
        {
            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            NativeMethods.SetInformationJobObject(_hJob,
                                                  NativeMethods.JobObjectExtendedLimitInformation,
                                                  ref info,
                                                  Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }
    }

    public void Start(string exePath, string arguments, string workingDirectory,
        System.Collections.Generic.IReadOnlyDictionary<string, string> extraEnv = null)
    {
        if (_disposed) { return; }

        OutputWindowLogger.Info($"=== transport start exe={exePath} workdir={workingDirectory}");
        OutputWindowLogger.Debug($">>> args={arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Extra env (e.g. CLAUDE_CODE_SSE_PORT) overlaid on the inherited parent env
        // so the CLI connects to our MCP server (honoured since UseShellExecute=false).
        if (extraEnv != null)
        {
            foreach (var kv in extraEnv) { psi.EnvironmentVariables[kv.Key] = kv.Value; }
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                OutputWindowLogger.Warn($"[stderr] {e.Data}");
                ErrorLine?.Invoke(this, e.Data);
            }
        };

        _process.Start();
        _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };
        _process.BeginErrorReadLine();

        if (_hJob != IntPtr.Zero)
        {
            NativeMethods.AssignProcessToJobObject(_hJob, _process.Handle);
        }

        OutputWindowLogger.Info($"--- transport PID={_process.Id}");

        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "ClaudeNdjsonReader" };
        _readerThread.Start();
    }

    /// <summary>
    /// Serialise <paramref name="message"/> as a single NDJSON line on stdin.
    /// Accepts any object — anonymous types, POCOs, or already-built JObjects.
    /// </summary>
    public void Write(object message)
    {
        if (!IsRunning)
        {
            OutputWindowLogger.Warn("!!! transport.Write called but IsRunning=false");
            return;
        }
        try
        {
            var json = JsonConvert.SerializeObject(message, Formatting.None);
            OutputWindowLogger.Trace(() => $">>> stdin: {StringHelpers.Truncate(json, 500)}");
            // Lock so a worker-thread MCP response and a UI-thread write can't
            // interleave into one corrupt NDJSON line.
            lock (_writeLock)
            {
                _stdin.WriteLine(json);
                _stdin.Flush();
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("transport.Write", ex);
        }
    }

    private void ReadLoop()
    {
        OutputWindowLogger.Debug("--- transport ReadLoop started");
        // Capture the reader locally: Dispose() (respawn) nulls out _process on
        // another thread, and reading the field here would NRE the moment the
        // process exits and ReadLine() returns. The local keeps this loop tied
        // to the process it was started for.
        var reader = _process?.StandardOutput;
        if (reader == null) { return; }
        try
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var clean = line.Trim();
                OutputWindowLogger.Trace(() => $"<<< RAW: {(clean.Length <= 500 ? clean : clean.Substring(0, 500) + "...")}");
                if (clean.Length == 0 || clean[0] != '{') { continue; }
                JObject obj;
                try { obj = JObject.Parse(clean); }
                catch (Exception ex)
                {
                    OutputWindowLogger.Warn($"!!! transport parse error: {ex.Message}");
                    continue;
                }
                LineReceived?.Invoke(this, obj);
            }
            OutputWindowLogger.Debug("--- transport ReadLoop ended (stdout closed)");
        }
        catch (Exception ex)
        {
            // Closed stdout on a disposed respawn is expected — don't log it as a fault.
            if (_disposed) { OutputWindowLogger.Debug(() => $"--- transport read loop ended (disposed): {ex.GetType().Name}"); }
            else { OutputWindowLogger.LogException("transport.ReadLoop", ex); }
        }

        int exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; } catch { /* silent: process may already be gone */ }
        OutputWindowLogger.Info($"--- transport exit code={exitCode} disposed={_disposed}");

        if (!_disposed) { Exited?.Invoke(this, (exitCode, false)); }
        else { Exited?.Invoke(this, (exitCode, _intentional)); }
    }

    public void DisposeIntentional()
    {
        _intentional = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _stdin?.Close(); } catch { /* silent: cleanup */ }
        _stdin = null;
        try { if (_process?.HasExited == false) { _process.Kill(); } } catch { /* silent: cleanup */ }
        try { _process?.Dispose(); } catch { /* silent: cleanup */ }
        _process = null;
        if (_hJob != IntPtr.Zero) { NativeMethods.CloseHandle(_hJob); }
    }
}

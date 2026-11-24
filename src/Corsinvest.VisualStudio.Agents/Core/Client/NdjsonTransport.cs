/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// Low-level NDJSON transport: owns the claude.exe process and reads/writes one
/// JSON object per line on stdin/stdout. No protocol logic: caller dispatches lines.
/// </summary>
internal sealed class NdjsonTransport : IDisposable
{
    private Process _process;
    // Lines for stdin, drained by one writer thread. A write to the pipe blocks while claude.exe
    // is not reading stdin, which it stops doing while its own stdout is full: a caller that wrote
    // to the pipe itself, the reader thread answering a hook callback above all, would stop
    // draining that stdout and deadlock both processes (#364). One consumer also keeps two lines
    // from interleaving into one corrupt line.
    private readonly BlockingCollection<string> _outgoing = new();
    private Thread _readerThread;
    private Thread _writerThread;
    private readonly IntPtr _hJob;
    private volatile bool _disposed;

    public event EventHandler<JObject> LineReceived;
    public event EventHandler<string> ErrorLine;
    public event EventHandler<(int exitCode, bool intentional)> Exited;

    /// <summary>Marked when the caller intends to kill the process (e.g. Dispose for respawn).</summary>
    private bool _intentional;

    public int Pid => _process?.Id ?? -1;
    public bool IsRunning => _process?.HasExited == false;

    private readonly OutputWindowLogger _log;

    // Optional: the probes build a transport with no pane behind it (→ Global, unprefixed).
    public NdjsonTransport(OutputWindowLogger log = null)
    {
        _log = log ?? OutputWindowLogger.Global;
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

    /// <param name="dropEnv">Inherited variables to remove before <paramref name="extraEnv"/> goes
    /// on: see <see cref="ClaudeInstall.InheritedSessionEnvVars"/> for which, and why.</param>
    public void Start(string exePath, string arguments, string workingDirectory,
        System.Collections.Generic.IReadOnlyDictionary<string, string> extraEnv,
        System.Collections.Generic.IEnumerable<string> dropEnv = null)
    {
        if (_disposed) { return; }

        _log.Info($"=== transport start exe={exePath} workdir={workingDirectory}");
        _log.Debug(() => $">>> args={arguments}");

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
        // Drop first, then overlay: a key the caller sets must survive its own drop list. The
        // collection is case-insensitive, so an inherited variable goes whatever case it arrived in.
        if (dropEnv != null)
        {
            foreach (var key in dropEnv) { psi.EnvironmentVariables.Remove(key); }
        }
        // Extra env (profile keys, CLAUDE_CODE_ENTRYPOINT, …) overlaid on the inherited
        // parent env, honoured since UseShellExecute=false.
        if (extraEnv != null)
        {
            foreach (var kv in extraEnv) { psi.EnvironmentVariables[kv.Key] = kv.Value; }
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _log.Warn($"[stderr] {e.Data}");
                ErrorLine?.Invoke(this, e.Data);
            }
        };

        _process.Start();
        var stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };
        _process.BeginErrorReadLine();

        if (_hJob != IntPtr.Zero)
        {
            NativeMethods.AssignProcessToJobObject(_hJob, _process.Handle);
        }

        _log.Info($"--- transport PID={_process.Id}");

        _writerThread = new Thread(() => WriteLoop(stdin)) { IsBackground = true, Name = "ClaudeNdjsonWriter" };
        _writerThread.Start();
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "ClaudeNdjsonReader" };
        _readerThread.Start();
    }

    /// <summary>
    /// Serialise <paramref name="message"/> as a single NDJSON line and queue it for stdin.
    /// Never blocks on the pipe, so it is safe from the reader thread and the UI thread.
    /// Accepts any object: anonymous types, POCOs, or already-built JObjects.
    /// </summary>
    public void Write(object message)
    {
        try
        {
            var json = JsonConvert.SerializeObject(message, Formatting.None);
            _log.Trace(() => $">>> stdin: {StringHelpers.Truncate(json, 500)}");
            // Checked here, not only in the writer: a line queued for a dead process is never
            // sent, and the warning now is what tells a SendControlRequestAsync's caller why.
            if (_disposed || !IsRunning || !_outgoing.TryAdd(json))
            {
                _log.Warn("!!! transport.Write called but the process is gone");
            }
        }
        catch (InvalidOperationException)
        {
            // TryAdd after CompleteAdding: Dispose ran between the check and the add.
            _log.Warn("!!! transport.Write called but the process is gone");
        }
        catch (Exception ex)
        {
            _log.LogException("transport.Write", ex);
        }
    }

    private void WriteLoop(StreamWriter stdin)
    {
        try
        {
            foreach (var json in _outgoing.GetConsumingEnumerable())
            {
                stdin.WriteLine(json);
                // Flush once the queue is empty: a burst of lines goes out in one pipe write.
                if (_outgoing.Count == 0) { stdin.Flush(); }
            }
        }
        catch (Exception ex)
        {
            // A broken pipe after Dispose killed the process is expected: don't log it as a fault.
            if (_disposed) { _log.Debug(() => $"--- transport write loop ended (disposed): {ex.GetType().Name}"); }
            else { _log.LogException("transport.WriteLoop", ex); }
        }
        finally
        {
            // Nothing will drain the queue any more: refuse new lines instead of piling them up.
            _outgoing.CompleteAdding();
            try { stdin.Close(); } catch { /* silent: cleanup */ }
        }
    }

    private void ReadLoop()
    {
        _log.Debug(() => "--- transport ReadLoop started");
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
                _log.Trace(() => $"<<< RAW: {(clean.Length <= 500 ? clean : clean.Substring(0, 500) + "...")}");
                if (clean.Length == 0 || clean[0] != '{') { continue; }
                JObject obj;
                try { obj = JObject.Parse(clean); }
                catch (Exception ex)
                {
                    _log.Warn($"!!! transport parse error: {ex.Message}");
                    continue;
                }
                LineReceived?.Invoke(this, obj);
            }
            _log.Debug(() => "--- transport ReadLoop ended (stdout closed)");
        }
        catch (Exception ex)
        {
            // Closed stdout on a disposed respawn is expected: don't log it as a fault.
            if (_disposed) { _log.Debug(() => $"--- transport read loop ended (disposed): {ex.GetType().Name}"); }
            else { _log.LogException("transport.ReadLoop", ex); }
        }

        int exitCode = -1;
        try { exitCode = _process?.ExitCode ?? -1; } catch { /* silent: process may already be gone */ }
        _log.Info($"--- transport exit code={exitCode} disposed={_disposed}");

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
        // No join on the writer: it may be blocked on a pipe claude.exe is not reading. The kill
        // below breaks that pipe, the write fails and the writer closes stdin on its way out.
        // Lines still queued are dropped, as the process they were for is going away.
        _outgoing.CompleteAdding();
        try { if (_process?.HasExited == false) { _process.Kill(); } } catch { /* silent: cleanup */ }
        try { _process?.Dispose(); } catch { /* silent: cleanup */ }
        _process = null;
        if (_hJob != IntPtr.Zero) { NativeMethods.CloseHandle(_hJob); }
    }
}

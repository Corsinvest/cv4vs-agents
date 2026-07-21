/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Corsinvest.VisualStudio.Agents.Cli;

/// <summary>
/// Drives a child process running inside a <see cref="ConPty.Session"/>: a background thread
/// reads the pseudo-console output, decodes it as UTF-8 and hands it up in coalesced batches
/// (one flush ~every 16 ms, i.e. ≈60 fps, so a chatty child doesn't drown the UI thread in
/// events). Also forwards keystrokes and viewport resizes down to the child. One per CLI pane.
/// </summary>
internal sealed class TerminalProcess : IDisposable
{
    /// <summary>Coalesced UTF-8 output ready to render. Raised on the read thread.</summary>
    public event Action<string> OutputReceived;

    /// <summary>Raised once, when the output pipe reaches EOF (the child has exited).</summary>
    public event Action ProcessExited;

    private readonly object _gate = new();       // guards _session / lifecycle
    private readonly object _pending = new();     // guards the output batch buffer

    private ConPty.Session _session;
    private Thread _reader;
    private CancellationTokenSource _stop;
    private Decoder _decoder;                     // stateful: keeps split UTF-8 sequences between reads
    private Timer _flush;
    private readonly StringBuilder _batch = new();
    private bool _disposed;

    public bool IsRunning
    {
        get { lock (_gate) { return _session != null && !_disposed; } }
    }

    /// <summary>Child OS process id; 0 before <see cref="Start"/> or after <see cref="Dispose"/>.</summary>
    public int ProcessId
    {
        get { lock (_gate) { return _session?.ProcessId ?? 0; } }
    }

    /// <summary>
    /// Launch <paramref name="command"/> in a fresh ConPTY and begin streaming its output.
    /// A non-empty <paramref name="env"/> gives the child a per-process environment (see
    /// <see cref="ConPty.Create"/>). Throws if already started or disposed.
    /// </summary>
    public void Start(string command, string workingDirectory, short cols = 120, short rows = 40,
        IReadOnlyDictionary<string, string> env = null)
    {
        lock (_gate)
        {
            if (_disposed) { throw new ObjectDisposedException(nameof(TerminalProcess)); }
            if (_session != null) { throw new InvalidOperationException("Terminal already started."); }

            _stop = new CancellationTokenSource();
            _decoder = Encoding.UTF8.GetDecoder();
            _flush = new Timer(OnFlush, null, Timeout.Infinite, Timeout.Infinite);
            _session = ConPty.Create(command, workingDirectory, cols, rows, env);

            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"cv4vs ConPTY read (pid {_session.ProcessId})",
            };
            _reader.Start();
        }
    }

    /// <summary>Send <paramref name="text"/> (UTF-8) to the child's input.</summary>
    public void WriteInput(string text)
    {
        if (string.IsNullOrEmpty(text)) { return; }
        lock (_gate)
        {
            if (_session == null) { return; }
            var bytes = Encoding.UTF8.GetBytes(text);
            ConPty.Write(_session.InputWriteHandle, bytes, bytes.Length);
        }
    }

    /// <summary>Resize the child's viewport. No-op for non-positive dimensions or before start.</summary>
    public void Resize(short cols, short rows)
    {
        if (cols <= 0 || rows <= 0) { return; }
        lock (_gate)
        {
            if (_session == null) { return; }
            ConPty.Resize(_session.PseudoConsoleHandle, cols, rows);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var token = _stop.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                IntPtr output;
                lock (_gate)
                {
                    if (_session == null) { break; }
                    output = _session.OutputReadHandle;
                }

                var count = ConPty.Read(output, buffer);
                if (count <= 0) { break; }   // EOF → child gone

                var chars = new char[_decoder.GetCharCount(buffer, 0, count)];
                _decoder.GetChars(buffer, 0, count, chars, 0);

                lock (_pending)
                {
                    _batch.Append(chars);
                    _flush?.Change(16, Timeout.Infinite);   // debounce → ~60 fps
                }
            }
        }
        catch when (token.IsCancellationRequested) { /* expected on shutdown */ }
        catch { /* broken pipe / child died mid-read */ }

        OnFlush(null);           // emit the tail before announcing exit
        ProcessExited?.Invoke();
    }

    private void OnFlush(object _)
    {
        string text;
        lock (_pending)
        {
            if (_batch.Length == 0) { return; }
            text = _batch.ToString();
            _batch.Clear();
        }
        OutputReceived?.Invoke(text);
    }

    public void Dispose()
    {
        Thread reader;
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;

            _stop?.Cancel();
            _flush?.Dispose();
            _flush = null;

            var session = _session;
            _session = null;
            session?.Dispose();      // closes the pipes → unblocks the read thread

            _stop?.Dispose();
            _stop = null;

            reader = _reader;
            _reader = null;
        }

        // Join outside the lock: the read thread takes _gate, so holding it here would deadlock.
        reader?.Join(1000);
    }
}

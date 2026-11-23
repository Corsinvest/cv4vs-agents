/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>
/// Regression for the VS freeze (#364): a LineReceived handler that answers on stdin must
/// never stall the drain of claude.exe's stdout. The transport spawns a child (PowerShell)
/// that writes a small line, then a 0.9 MB one, then two 0.3 MB ones, and — only once all
/// three big writes have been consumed from the pipe — a marker line on stderr. The handler
/// for the 0.9 MB line writes a 0.2 MB ack back that the child never reads, so that Flush
/// blocks on the full stdin pipe, exactly like a control-request answer while claude.exe is
/// busy writing stdout. With the handler running on the reader thread (the old code) the
/// reader is stuck in that Flush, the 0.3 MB lines never drain, the child never reaches the
/// marker and the test times out; with the queue + dispatcher the reader keeps pulling lines.
/// </summary>
public class NdjsonTransportTests
{
    private const string Marker = "MARKER-STDOUT-DRAINED";

    private static string EncodeScript(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    [Fact]
    public void Handler_that_writes_back_does_not_stall_stdout_drain()
    {
        var script = "$o=[Console]::Out; $e=[Console]::Error\n" +
                     "$o.WriteLine('{\"small\":1}')\n" +
                     "$o.WriteLine('{\"big\":\"' + ('x'*900000) + '\"}')\n" +
                     "$o.WriteLine('{\"b\":\"' + ('y'*300000) + '\"}')\n" +
                     "$o.WriteLine('{\"c\":\"' + ('z'*300000) + '\"}')\n" +
                     "$e.WriteLine('" + Marker + "')\n" +
                     "Start-Sleep -Seconds 60\n";

        var transport = new NdjsonTransport();
        try
        {
            var smallSeen = new ManualResetEventSlim(false);
            var drained = new ManualResetEventSlim(false);
            transport.LineReceived += (_, obj) =>
            {
                if (obj.Property("small") != null)
                {
                    smallSeen.Set();
                    // A handler answering on stdin, as the hook callback does.
                    transport.Write(new { ack = "small" });
                }
                else if (obj.Property("big") != null)
                {
                    // 0.2 MB into a stdin nobody reads: this Flush blocks (pipe full),
                    // reproducing the blocked Write the freeze died on.
                    transport.Write(new { ack = new string('a', 200_000) });
                }
                // "b"/"c": nothing, just prove they get drained off the pipe.
            };
            transport.ErrorLine += (_, line) => { if (line != null && line.Contains(Marker)) { drained.Set(); } };

            transport.Start("powershell.exe",
                            "-NoProfile -NonInteractive -EncodedCommand " + EncodeScript(script),
                            Path.GetTempPath(), null);

            Assert.True(smallSeen.Wait(TimeSpan.FromSeconds(20)), "small line never delivered");
            Assert.True(drained.Wait(TimeSpan.FromSeconds(20)),
                        "stdout stalled after a handler blocked on a stdin write: the deadlock is back");
        }
        finally
        {
            // Break the pipes so the handler's blocked Flush unwinds before Dispose
            // (Dispose needs _writeLock, which the blocked Flush holds).
            try { Process.GetProcessById(transport.Pid).Kill(); } catch { /* silent */ }
            var sw = Stopwatch.StartNew();
            while (transport.IsRunning && sw.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(100); }
            transport.Dispose();
        }
    }
}

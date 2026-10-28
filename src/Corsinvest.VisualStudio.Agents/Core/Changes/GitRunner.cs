/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>What a git command printed and how it ended.</summary>
internal sealed class GitResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public bool TimedOut { get; set; }

    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>Where the <c>git</c> to run is. The user's own, on PATH, comes first: it is the one whose
/// configuration they set up, and the one the rest of their tools agree with. Visual Studio's bundled
/// copy (Team Explorer) is the fallback, so a machine with no Git installed still has one.</summary>
internal static class GitExe
{
    private static string _found;

    /// <summary>The path of a git executable, or null when there is none. A path that was found is
    /// remembered (until the file goes); "none" is not, so a git installed while Visual Studio runs is
    /// found by the next chat instead of after a restart. It probes the disk: not for the UI thread.</summary>
    public static string Resolve()
    {
        var known = Volatile.Read(ref _found);
        if (known is not null && File.Exists(known)) { return known; }

        var found = Find();
        Volatile.Write(ref _found, found);
        return found;
    }

    internal static string Find()
    {
        var onPath = OnPath("git.exe");
        if (onPath is not null) { return onPath; }

        try
        {
            var devenv = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(devenv))
            {
                var bundled = Path.Combine(Path.GetDirectoryName(devenv), "CommonExtensions", "Microsoft",
                                           "TeamFoundation", "Team Explorer", "Git", "cmd", "git.exe");
                if (File.Exists(bundled)) { return bundled; }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // No main module to look next to: only PATH was ever going to answer.
        }
        return null;
    }

    private static string OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) { continue; }
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(candidate)) { return candidate; }
            }
            catch (ArgumentException)
            {
                // A PATH entry that is not a path.
            }
        }
        return null;
    }
}

/// <summary>Runs git without a window and without waiting on the UI thread. Every call has a time
/// limit: a git that hangs (a credential prompt, a network drive) must not hold up a hook the CLI is
/// waiting on, and is killed rather than left running.</summary>
/// <param name="drainLimit">How long to wait for the output once git has gone: null = ten seconds, or two
/// when it was killed. A test sets it short.</param>
internal sealed class GitRunner(string exe, OutputWindowLogger log, TimeSpan? drainLimit = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string Exe => exe;

    /// <summary>Run git and collect what it printed (UTF-8). <paramref name="stdin"/> is written as
    /// UTF-8 bytes and closed.</summary>
    public async Task<GitResult> RunAsync(string workingDirectory, IEnumerable<string> args,
                                          IDictionary<string, string> env = null, TimeSpan? timeout = null,
                                          string stdin = null, CancellationToken cancel = default)
    {
        using var process = Start(workingDirectory, args, env, stdin is not null);
        if (process is null) { return new GitResult { ExitCode = -1, Error = "git could not be started" }; }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Feed(process, stdin).ConfigureAwait(false);
        var timedOut = !await Exit(process, timeout ?? DefaultTimeout, cancel).ConfigureAwait(false);
        var drained = await Drained(timedOut, output, error).ConfigureAwait(false);

        return new GitResult
        {
            ExitCode = timedOut ? -1 : process.ExitCode,
            Output = drained && output.Status == TaskStatus.RanToCompletion ? output.Result : "",
            Error = error.Status == TaskStatus.RanToCompletion ? error.Result : "",
            TimedOut = timedOut,
        };
    }

    /// <summary>Run git and write its standard output, byte for byte, to a file. For a blob: the
    /// reading side of a pipe that decodes would corrupt anything that is not text.</summary>
    public async Task<GitResult> RunToFileAsync(string workingDirectory, IEnumerable<string> args, string targetFile,
                                                IDictionary<string, string> env = null, TimeSpan? timeout = null,
                                                CancellationToken cancel = default)
    {
        using var process = Start(workingDirectory, args, env, false);
        if (process is null) { return new GitResult { ExitCode = -1, Error = "git could not be started" }; }

        var error = process.StandardError.ReadToEndAsync();
        using var file = await AsyncFile.CreateAsync(targetFile).ConfigureAwait(false);
        // Copied while the wait below runs, so a git that stops talking is still caught by the time
        // limit; a killed process closes its pipe, which ends the copy.
        var copy = process.StandardOutput.BaseStream.CopyToAsync(file);
        var timedOut = !await Exit(process, timeout ?? DefaultTimeout, cancel).ConfigureAwait(false);
        var drained = await Drained(timedOut, copy, error).ConfigureAwait(false);

        return new GitResult
        {
            // A copy that never finished is a file that is not all there: not a success, whatever git said.
            ExitCode = timedOut || !drained ? -1 : process.ExitCode,
            Error = error.Status == TaskStatus.RanToCompletion ? error.Result : "",
            TimedOut = timedOut,
        };
    }

    // The pipes close when git exits, unless something git started (a filter such as git-lfs, a credential
    // helper) inherited them and is still running: killing git does not end that, and reading to the end
    // would then wait for it with nothing to stop it. Whatever came through is kept; the rest is let go.
    private async Task<bool> Drained(bool killed, params Task[] readers)
    {
        var all = Task.WhenAll(readers);
        var limit = drainLimit ?? (killed ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(10));
        if (await Task.WhenAny(all, Task.Delay(limit)).ConfigureAwait(false) == all) { return !all.IsFaulted; }

        log.Warn("[git] a program started by git kept its output open: not waiting for it");
        return false;
    }

    private Process Start(string workingDirectory, IEnumerable<string> args, IDictionary<string, string> env, bool redirectInput)
    {
        var info = new ProcessStartInfo(exe)
        {
            Arguments = string.Join(" ", args.Select(Helpers.StringHelpers.QuoteProcessArgument)),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // Never ask a person for anything: a prompt in a window nobody sees is a hang.
        info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        if (env is not null)
        {
            foreach (var kv in env) { info.EnvironmentVariables[kv.Key] = kv.Value; }
        }

        try { return Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"[git] could not start {exe}: {ex.Message}");
            return null;
        }
    }

    private static async Task Feed(Process process, string stdin)
    {
        if (stdin is null) { return; }
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // git exited before reading it all: its exit code says why.
        }
    }

    private static async Task<bool> Exit(Process process, TimeSpan timeout, CancellationToken cancel)
    {
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => finished.TrySetResult(true);
        if (process.HasExited) { finished.TrySetResult(true); }

        var done = await Task.WhenAny(finished.Task, Task.Delay(timeout, cancel)).ConfigureAwait(false);
        if (done == finished.Task)
        {
            // The streams drain after the exit event: give the process object its final word.
            process.WaitForExit();
            return true;
        }

        try { process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // It ended on its own while we decided.
        }
        return false;
    }
}

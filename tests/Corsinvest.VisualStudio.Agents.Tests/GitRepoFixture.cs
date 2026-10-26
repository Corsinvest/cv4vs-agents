/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>A throwaway git working tree for the tests that need the real thing: what git does with
/// line endings and ignore rules is the point, and a fake would only agree with itself.
/// <para>The tests skip themselves (pass without asserting) where there is no git to run, which is
/// <see cref="Available"/>; CI machines and developer machines have it.</para></summary>
internal sealed class GitRepoFixture : IDisposable
{
    public static bool Available => GitExe.Resolve() is not null;

    public string Root { get; }
    public string Scratch { get; }

    public GitRepoFixture(bool autocrlf = false)
    {
        Scratch = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Scratch, "repo");
        Directory.CreateDirectory(Root);
        Git("init", "-q");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "core.autocrlf", autocrlf ? "true" : "false");
        // Tests run on a developer's machine: their global config must not change the outcome.
        Git("config", "commit.gpgsign", "false");
    }

    public string Path_(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content, bool crlf = false)
    {
        var path = Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(crlf ? content.Replace("\n", "\r\n") : content));
        return path;
    }

    public void Commit(string message = "commit")
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    /// <summary>Run git in the working tree and return what it printed. Fails the test on a bad exit.</summary>
    public string Git(params string[] args) => GitIn(Root, args);

    /// <summary>Run git in another folder (a repository nested in this one, a linked worktree).</summary>
    public string GitIn(string directory, params string[] args)
    {
        var info = new ProcessStartInfo(GitExe.Resolve())
        {
            Arguments = string.Join(" ", Array.ConvertAll(args, Corsinvest.VisualStudio.Agents.Helpers.StringHelpers.QuoteProcessArgument)),
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) { throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {error}"); }
        return output;
    }

    public void Dispose()
    {
        try { Directory.Delete(Scratch, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only pack files can resist; it is a temp folder.
            foreach (var file in Directory.GetFiles(Scratch, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception) { /* best effort */ }
            }
            try { Directory.Delete(Scratch, true); }
            catch (Exception) { /* left for the OS temp cleaner */ }
        }
    }
}

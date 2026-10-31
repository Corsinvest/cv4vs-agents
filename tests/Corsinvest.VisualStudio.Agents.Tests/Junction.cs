/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Diagnostics;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>A real NTFS junction, for the tests of paths that reach a folder by two names. Needs no privilege, unlike a symbolic link.
/// <para>Removed before the folder around it is: a recursive delete that meets a junction first may follow it, and delete the
/// target's files.</para></summary>
internal static class Junction
{
    /// <summary>Make <paramref name="link"/> a junction to <paramref name="target"/>. Disposing it removes the link only.</summary>
    public static IDisposable Create(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info);
        var said = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) { throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {said}"); }
        return new Link(link);
    }

    private sealed class Link(string path) : IDisposable
    {
        // Not recursive: that removes the junction, and leaves what it points to alone.
        public void Dispose()
        {
            try { Directory.Delete(path, false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* already gone */ }
        }
    }
}

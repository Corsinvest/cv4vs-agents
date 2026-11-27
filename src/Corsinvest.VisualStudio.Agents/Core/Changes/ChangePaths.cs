/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Security;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Turns the several spellings of a file's path (a tool's argument, the CLI's tracking
/// path, the directory it resolved) into one key, and decides when two of them are the same file.</summary>
internal static class ChangePaths
{
    /// <summary>The absolute, normalised form of <paramref name="path"/>, resolved against
    /// <paramref name="baseDirectory"/> when relative. Null for something that cannot be a path.</summary>
    public static string Normalize(string path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        try
        {
            var p = path.Trim();
            if (!Path.IsPathRooted(p))
            {
                if (string.IsNullOrEmpty(baseDirectory)) { return null; }
                p = Path.Combine(baseDirectory, p);
            }
            return Path.GetFullPath(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>The file a <c>file-history</c> record stands for.
    /// <para>The CLI shortens a path under the working directory to a relative one and also records
    /// the directory it really lives in (<c>realParentDir</c>, with links resolved). The relative
    /// spelling is what the tool calls use, so it wins; the resolved directory is the way out when the
    /// working directory is not the one the CLI used (a <c>cd</c> moved it) and the relative path points
    /// nowhere.</para></summary>
    public static string Resolve(string trackingPath, string realParentDir, string baseDirectory)
    {
        var viaTracking = Normalize(trackingPath, baseDirectory);
        var viaReal = ViaRealDirectory(trackingPath, realParentDir);
        if (viaTracking is null) { return viaReal; }
        if (viaReal is null || Path.IsPathRooted(trackingPath)) { return viaTracking; }

        var parent = Path.GetDirectoryName(viaTracking);
        return parent is not null && Directory.Exists(parent) ? viaTracking : viaReal;
    }

    /// <summary>True when <paramref name="hookPath"/>, as a tool call spelled it, is the file the
    /// record describes under either of its two spellings.</summary>
    public static bool Matches(string hookPath, string trackingPath, string realParentDir, string baseDirectory)
    {
        var wanted = Normalize(hookPath, baseDirectory);
        if (wanted is null) { return false; }
        return Same(wanted, Normalize(trackingPath, baseDirectory))
            || Same(wanted, ViaRealDirectory(trackingPath, realParentDir));
    }

    public static bool Same(string a, string b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string ViaRealDirectory(string trackingPath, string realParentDir)
    {
        if (string.IsNullOrWhiteSpace(realParentDir) || string.IsNullOrWhiteSpace(trackingPath)) { return null; }
        try { return Path.GetFullPath(Path.Combine(realParentDir.Trim(), Path.GetFileName(trackingPath.Trim()))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}

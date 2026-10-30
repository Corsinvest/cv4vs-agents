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

    /// <summary>As <see cref="Normalize(string, string)"/>, in the spelling of the pane's working directory: a path the CLI or git
    /// reported through the real path of a linked folder comes out through the link, where the IDE has its documents.</summary>
    public static string Normalize(string path, string baseDirectory, PaneRoot root)
        => (root ?? PaneRoot.None).ToLogical(Normalize(path, baseDirectory));

    /// <summary>The file a <c>file-history</c> record stands for, in the working directory's spelling (see <see cref="PaneRoot"/>).
    /// <para>The CLI shortens a path under the working directory to a relative one and also records
    /// the directory it really lives in (<c>realParentDir</c>, with links resolved). The relative
    /// spelling is what the tool calls use, so it wins; the resolved directory is the way out when the
    /// working directory is not the one the CLI used (a <c>cd</c> moved it) and the relative path points
    /// nowhere.</para></summary>
    public static string Resolve(string trackingPath, string realParentDir, string baseDirectory, PaneRoot root = null)
    {
        var viaTracking = Normalize(trackingPath, baseDirectory);
        var viaReal = ViaRealDirectory(trackingPath, realParentDir);
        string resolved;
        if (viaTracking is null) { resolved = viaReal; }
        else if (viaReal is null || Path.IsPathRooted(trackingPath)) { resolved = viaTracking; }
        else
        {
            var parent = Path.GetDirectoryName(viaTracking);
            resolved = parent is not null && Directory.Exists(parent) ? viaTracking : viaReal;
        }
        return (root ?? PaneRoot.None).ToLogical(resolved);
    }

    /// <summary>True when <paramref name="hookPath"/>, as a tool call spelled it, is the file the
    /// record describes under either of its two spellings. The three paths are compared in the working directory's spelling, so
    /// that a hook reporting the real path of a linked folder still finds a record made under the link, and the other way round.</summary>
    public static bool Matches(string hookPath, string trackingPath, string realParentDir, string baseDirectory, PaneRoot root = null)
    {
        root ??= PaneRoot.None;
        var wanted = root.ToLogical(Normalize(hookPath, baseDirectory));
        if (wanted is null) { return false; }
        return Same(wanted, root.ToLogical(Normalize(trackingPath, baseDirectory)))
            || Same(wanted, root.ToLogical(ViaRealDirectory(trackingPath, realParentDir)));
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

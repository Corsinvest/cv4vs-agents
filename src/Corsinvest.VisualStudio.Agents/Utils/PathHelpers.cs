/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents;

internal static class PathHelpers
{
    /// <summary>
    /// Returns <paramref name="path"/> relative to <paramref name="root"/>,
    /// using forward slashes. Inputs may use either separator (or be mixed)
    /// and may or may not have a trailing slash.
    /// <list type="bullet">
    ///   <item>Empty string when <paramref name="path"/> equals <paramref name="root"/>.</item>
    ///   <item>The relative path (forward-slashed) when path lives inside root.</item>
    ///   <item>The original (forward-slashed) path when it lies outside root.</item>
    /// </list>
    /// </summary>
    public static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(path)) { return ""; }
        if (string.IsNullOrEmpty(root)) { return path.Replace('\\', '/'); }

        // Normalize separators + drop trailing slash so the StartsWith is unambiguous.
        var rootBare = root.Replace('/', '\\').TrimEnd('\\');
        var pathBare = path.Replace('/', '\\').TrimEnd('\\');

        if (pathBare.Equals(rootBare, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }
        var rootPrefix = rootBare + '\\';
        var rel = pathBare.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? pathBare.Substring(rootPrefix.Length)
            : pathBare;
        return rel.Replace('\\', '/');
    }

    /// <summary>Lowercase a Windows drive letter (<c>C:\…</c> → <c>c:\…</c>),
    /// leaving everything else untouched. The Claude CLI compares its
    /// <c>process.cwd()</c> (lowercase drive on Windows) against the lock
    /// file's <c>workspaceFolders</c> case-sensitively, so paths handed to it
    /// must use the lowercase-drive form or discovery fails.</summary>
    public static string LowercaseDrive(string path)
    {
        if (string.IsNullOrEmpty(path)) { return path; }
        return path.Length >= 2 && path[1] == ':' && char.IsUpper(path[0]) ? char.ToLowerInvariant(path[0]) + path.Substring(1) : path;
    }

    /// <summary>Convert a local path to a <c>file:///</c> URI in the exact
    /// shape the Claude CLI / LSP expect: forward slashes, lowercase drive.
    /// Returns the input unchanged when null/empty.</summary>
    public static string ToFileUri(string path)
    {
        if (string.IsNullOrEmpty(path)) { return path; }
        var normalized = LowercaseDrive(path.Replace('\\', '/'));
        return "file:///" + normalized.TrimStart('/');
    }

    /// <summary>Convert a <c>file:///</c> URI back to a local path. Non-URI
    /// inputs (already a path) are returned unchanged.</summary>
    public static string FromFileUri(string s)
    {
        if (string.IsNullOrEmpty(s)) { return s; }
        if (s.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            try { return new Uri(s).LocalPath; } catch { return s; }
        }
        return s;
    }

    /// <summary>True when two values denote the same file, comparing as
    /// <c>file:///</c> URIs (each side normalised first, so a raw path and a
    /// URI for the same file match). Case-insensitive.</summary>
    public static bool UrisEquivalent(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) { return false; }
        var na = a.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? a : ToFileUri(a);
        var nb = b.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? b : ToFileUri(b);
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
}

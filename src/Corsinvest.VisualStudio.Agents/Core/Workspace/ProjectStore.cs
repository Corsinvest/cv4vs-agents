/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Workspace;

/// <summary>Our per-project data folder: what it is called, and the file that says which project it
/// holds. The two belong together — the name carries a hash and cannot be read back, so a folder
/// that loses its descriptor is anonymous for good.
/// <para>The name used to be the whole working directory with its separators replaced, which ran
/// past the 248 characters .NET Framework allows a directory: the stats cache then failed to save
/// on every attempt, silently, and the statistics re-read every .jsonl each time they opened.</para>
/// </summary>
internal static class ProjectStore
{
    private const string DescriptorFile = "project.json";

    /// <summary>Longest tail kept in the folder name. The tail is there to be recognised by eye —
    /// the hash is what makes the name unique — so it only has to be long enough for that.</summary>
    private const int MaxTail = 30;

    /// <summary>Hash characters kept. Eight hex digits, with the tail already telling most projects
    /// apart, puts a collision far below anything one machine will produce.</summary>
    private const int HashChars = 8;

    /// <summary>The folder name for a working directory: its last segment, plus a hash of the whole
    /// path. Pure — touches nothing.
    /// <para>The tail, where the CLI truncates from the head: projects on one machine share a long
    /// prefix and differ at the end, so cutting from the front would leave them looking identical.
    /// The hash covers the full path, so two projects with the same tail under different roots
    /// still land in different folders.</para></summary>
    public static string FolderName(string workingDirectory)
    {
        var full = Path.GetFullPath(workingDirectory);
        var hash = ShortHash(full);
        var tail = Sanitize(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar,
                                                          Path.AltDirectorySeparatorChar)));
        if (tail.Length > MaxTail) { tail = tail.Substring(0, MaxTail); }
        // A root like "K:\" has no tail to show; the hash alone still identifies it.
        return tail.Length == 0 ? hash : $"{tail}-{hash}";
    }

    /// <summary>The project's data folder, created, descriptor written. Written here and nowhere
    /// else, so a folder cannot come into being without one.</summary>
    public static string FolderFor(string workingDirectory)
    {
        var full = Path.GetFullPath(workingDirectory);
        var folder = Path.Combine(AppPaths.ProjectsRoot, FolderName(full));
        try
        {
            Directory.CreateDirectory(folder);
            var descriptor = Path.Combine(folder, DescriptorFile);
            // Only when missing: the path that produced this folder cannot change without changing
            // the hash, so a descriptor that is already there is never stale.
            if (!File.Exists(descriptor))
            {
                File.WriteAllText(descriptor, new JObject { ["path"] = full }.ToString());
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("ProjectStore.FolderFor", ex); }
        return folder;
    }

    /// <summary>The project a folder belongs to. Null when the descriptor is missing or unreadable —
    /// there is no other way back, the name being a one-way hash.</summary>
    public static string PathOf(string projectFolder)
    {
        if (string.IsNullOrEmpty(projectFolder)) { return null; }
        try
        {
            var descriptor = Path.Combine(projectFolder, DescriptorFile);
            return File.Exists(descriptor)
                    ? JObject.Parse(File.ReadAllText(descriptor)).Val("path", (string)null)
                    : null;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("ProjectStore.PathOf", ex);
            return null;
        }
    }

    /// <summary>Every project folder on disk. For inspection, and for pruning the ones whose project
    /// is gone — which nothing does yet.</summary>
    public static IEnumerable<string> All()
        => Directory.Exists(AppPaths.ProjectsRoot)
            ? Directory.GetDirectories(AppPaths.ProjectsRoot)
            : [];

    private static string Sanitize(string name)
        => new(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>Lower-cased before hashing: Windows paths are case-insensitive, so K:\Source and
    /// k:\source are one project and have to reach one folder.</summary>
    private static string ShortHash(string value)
    {
        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        var sb = new StringBuilder(HashChars + 2);
        for (var i = 0; sb.Length < HashChars && i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString(0, HashChars);
    }
}

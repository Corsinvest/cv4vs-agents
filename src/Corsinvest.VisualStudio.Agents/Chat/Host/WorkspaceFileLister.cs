/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>What one listing produced: the workspace-relative paths, or why there are none.</summary>
internal sealed class FileListing
{
    private FileListing(IReadOnlyList<string> paths, string failure, string warning, long elapsedMs)
    {
        Paths = paths;
        Failure = failure;
        Warning = warning;
        ElapsedMs = elapsedMs;
    }

    public IReadOnlyList<string> Paths { get; }
    public string Failure { get; }
    /// <summary>A folder that could not be read (a locked one) while the rest was listed.</summary>
    public string Warning { get; }
    public long ElapsedMs { get; }
    public bool Ok => Failure == null;

    public static FileListing Success(IReadOnlyList<string> paths, long elapsedMs, string warning) => new(paths, null, warning, elapsedMs);
    public static FileListing Failed(string reason, long elapsedMs) => new([], reason, null, elapsedMs);
}

/// <summary>Lists a workspace's files for the `@` picker: `.gitignore` files at every level of the
/// workspace (never above it) plus git's global excludes, then the picker's own rules where those say
/// nothing. Sorted, forward-slashed, relative to the workspace.
/// <para>FindFirstFileEx rather than Directory.EnumerateFiles: on .NET Framework the latter asks for
/// 8.3 names, fetches in small batches and validates every path — 2.6 s against 0.3 s for the same
/// 120,000 files. Folders are read level by level on the thread pool, since matching the rules is
/// the CPU part.</para></summary>
internal static class WorkspaceFileLister
{
    public static Task<FileListing> ListAsync(string root, bool useGitIgnore, CancellationToken ct)
        => Task.Run(() => List(root, GitIgnoreCache.GetConfigured(), useGitIgnore, ct), ct);

    internal static Task<FileListing> ListAsync(string root, GitIgnore configured, bool useGitIgnore, CancellationToken ct)
        => Task.Run(() => List(root, configured, useGitIgnore, ct), ct);

    /// <summary>One folder to read: where it is, its path relative to the workspace, the .gitignore
    /// files that apply to it (outermost first, each with the folder it came from), and whether an
    /// ancestor was excluded and walked only because a `!` rule may re-include something below it.</summary>
    private sealed class Folder(string full, string rel, (string Base, GitIgnore Rules)[] chain, bool excluded)
    {
        public string Full { get; } = full;
        public string Rel { get; } = rel;
        public (string Base, GitIgnore Rules)[] Chain { get; } = chain;
        public bool Excluded { get; } = excluded;
    }

    private static FileListing List(string root, GitIgnore configured, bool useGitIgnore, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string rootFull;
        try { rootFull = Path.GetFullPath(root).TrimEnd('\\'); }
        catch (Exception ex) { return FileListing.Failed($"invalid workspace path: {ex.Message}", sw.ElapsedMilliseconds); }
        if (!Directory.Exists(rootFull)) { return FileListing.Failed($"workspace not found: {rootFull}", sw.ElapsedMilliseconds); }

        var rootRules = useGitIgnore ? GitIgnoreCache.Get(rootFull) : null;
        var chain = rootRules == null ? [] : new[] { ("", rootRules) };
        var files = new ConcurrentBag<List<string>>();
        string warning = null;
        var level = new List<Folder> { new(rootFull, "", chain, excluded: false) };
        var options = new ParallelOptions { CancellationToken = ct };

        while (level.Count > 0)
        {
            var next = new ConcurrentBag<Folder>();
            Parallel.ForEach(level, options, folder =>
            {
                var entries = Read(folder.Full, out var error);
                if (entries == null)
                {
                    // One locked folder costs its own contents, not the whole picker.
                    if (folder.Rel.Length > 0) { Interlocked.CompareExchange(ref warning, $"{folder.Rel}: {error}", null); }
                    return;
                }

                // A folder's own .gitignore governs its own entries, so it joins the chain first.
                var here = folder.Chain;
                if (useGitIgnore && folder.Rel.Length > 0 && entries.Any(e => !e.IsDir && e.Name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)))
                {
                    var nested = GitIgnoreCache.GetNested(folder.Full);
                    if (nested != null)
                    {
                        // Copied, not appended: sibling folders share the parent's array. Spelled
                        // out because a spread collection expression pulls in
                        // System.Runtime.CompilerServices.Unsafe on .NET Framework.
                        var longer = new (string Base, GitIgnore Rules)[here.Length + 1];
                        Array.Copy(here, longer, here.Length);
                        longer[here.Length] = (folder.Rel, nested);
                        here = longer;
                    }
                }

                var listed = new List<string>();
                foreach (var (name, isDir) in entries)
                {
                    var rel = folder.Rel.Length == 0 ? name : folder.Rel + "/" + name;
                    var ignored = IsIgnored(rel, isDir, here, configured, folder.Excluded, out var decided);
                    if (!isDir)
                    {
                        if (!ignored) { listed.Add(rel); }
                        continue;
                    }
                    if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) { continue; }
                    // An excluded folder is still walked when a `!` rule may bring something inside
                    // it back (`.vscode/*` + `!.vscode/settings.json`); what it holds stays excluded
                    // unless a rule says otherwise.
                    if (ignored && !Keeps(rel, here, configured)) { continue; }
                    next.Add(new Folder(folder.Full + "\\" + name, rel, here, ignored || (folder.Excluded && !decided)));
                }
                files.Add(listed);
            });
            level = [.. next];
        }

        var paths = files.SelectMany(f => f).ToList();
        // Folders finish in whatever order their threads do; sorted, the picker's row cap keeps the
        // same prefix on every opening.
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return FileListing.Success(paths, sw.ElapsedMilliseconds, warning);
    }

    /// <summary>Deepest .gitignore first, then the picker's rules; a folder excluded above and walked
    /// only for a negation passes its exclusion down to whatever no rule mentions.</summary>
    private static bool IsIgnored(string rel, bool isDir, (string Base, GitIgnore Rules)[] chain, GitIgnore configured, bool inheritedExcluded, out bool decided)
    {
        for (var i = chain.Length - 1; i >= 0; i--)
        {
            if (chain[i].Rules.TryMatch(RelativeTo(rel, chain[i].Base), isDir, out var ignored)) { decided = true; return ignored; }
        }
        if (configured != null && configured.TryMatch(rel, isDir, out var byConfigured)) { decided = true; return byConfigured; }
        decided = false;
        return inheritedExcluded;
    }

    private static bool Keeps(string rel, (string Base, GitIgnore Rules)[] chain, GitIgnore configured)
        => chain.Any(c => c.Rules.KeepsSubtree(RelativeTo(rel, c.Base))) || (configured?.KeepsSubtree(rel) ?? false);

    private static string RelativeTo(string rel, string baseRel) => baseRel.Length == 0 ? rel : rel.Substring(baseRel.Length + 1);

    /// <summary>The folder's entries, or null with the reason when it cannot be opened.</summary>
    private static List<(string Name, bool IsDir)> Read(string full, out string error)
    {
        error = null;
        var handle = FindFirstFileEx(LongPath(full) + "\\*", FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
        if (handle == InvalidHandle)
        {
            error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }
        var entries = new List<(string, bool)>();
        try
        {
            do
            {
                var name = data.cFileName;
                if (name == "." || name == "..") { continue; }
                var isDir = (data.dwFileAttributes & FileAttributeDirectory) != 0;
                // Junctions and directory links are not followed: two pointing back up make the walk
                // branch at every level and never end, and one pointing outside pulls another tree
                // in. Other reparse points stay — OneDrive and cloud-file folders are ones.
                if (isDir && (data.dwFileAttributes & FileAttributeReparsePoint) != 0
                    && (data.dwReserved0 == IoReparseTagMountPoint || data.dwReserved0 == IoReparseTagSymlink))
                {
                    continue;
                }
                entries.Add((name, isDir));
            } while (FindNextFile(handle, out data));
        }
        finally { FindClose(handle); }
        return entries;
    }

    // `\\?\` lifts the 260-character limit; a UNC path takes the `\\?\UNC\` form.
    private static string LongPath(string full)
        => full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
           : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2)
           : @"\\?\" + full;

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    // With FindExInfoBasic, dwReserved0 carries the reparse tag of a reparse point.
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint IoReparseTagSymlink = 0xA000000C;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileEx(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFile(IntPtr hFindFile, out Win32FindData lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);
}

/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>What git holds for a path in a tree.</summary>
internal sealed class TreeBlob
{
    public string Mode { get; set; }
    public string Oid { get; set; }

    /// <summary>A plain file. A symlink (120000) or a submodule (160000) is not something to restore
    /// by writing bytes.</summary>
    public bool IsRegularFile => Mode == "100644" || Mode == "100755";
}

/// <summary>A picture of a working tree at a moment, kept as a git tree object: the "everything goes
/// into a snapshot, then compared" that tells what a shell command changed and what a file was before it.
/// <para>Taken with a private index and a private object store, so nothing is written into the user's
/// repository: <c>git add -A</c> runs against a copy of the repo's index (seeded once, so the stat cache
/// is warm and only files that differ are read) and writes its new objects into this folder, with the
/// repo's own object store as an alternate, so the blobs git already has are reused rather than stored twice.</para>
/// <para>Content is read back through git's own filters (<c>cat-file --filters</c>): under
/// <c>core.autocrlf</c> or an <c>eol</c> attribute the blob is LF while the file on disk is CRLF, and writing
/// the raw blob back would change every line ending.</para></summary>
internal sealed class GitSnapshot : IDisposable
{
    private readonly GitRunner _git;
    private readonly string _repoObjects;
    private readonly string _repoIndex;
    private readonly string _folder;
    private readonly OutputWindowLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _treeLock = new();
    private string _tree;
    private volatile bool _disposed;

    private GitSnapshot(GitRunner git, string topLevel, string repoObjects, string repoIndex, string folder, OutputWindowLogger log)
    {
        _git = git;
        TopLevel = topLevel;
        _repoObjects = repoObjects;
        _repoIndex = repoIndex;
        _folder = folder;
        _log = log;
    }

    /// <summary>The working tree's root, absolute.</summary>
    public string TopLevel { get; }

    /// <summary>The newest tree taken, or null before the first one.</summary>
    public string LatestTree { get { lock (_treeLock) { return _tree; } } }

    /// <summary>Find the repository <paramref name="workingDirectory"/> is in. Null when it is in none:
    /// without one there is nothing to take a snapshot of.</summary>
    public static async Task<GitSnapshot> OpenAsync(GitRunner git, string workingDirectory, string folder, OutputWindowLogger log)
    {
        var found = await git.RunAsync(workingDirectory,
            new[] { "rev-parse", "--show-toplevel", "--git-path", "objects", "--git-path", "index" },
            timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!found.Ok) { return null; }

        var lines = found.Output.Split('\n').Select(l => l.Trim('\r')).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 3) { return null; }

        // Relative to where git ran, and in forward slashes: both made plain here.
        string Absolute(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        return new GitSnapshot(git, Absolute(lines[0]), Absolute(lines[1]), Absolute(lines[2]), folder, log);
    }

    /// <summary>Take a snapshot of the working tree as it is now. Returns its tree id, or null when git
    /// could not do it (logged). One at a time: two runs over the same index would trip over each other.</summary>
    public async Task<string> TakeAsync(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            // A picture requested just before the chat closed would otherwise remake the folder that
            // closing it removed.
            if (_disposed) { return null; }
            Directory.CreateDirectory(Path.Combine(_folder, "objects"));
            var seeded = SeedIndex();
            var tree = await AddAndWriteAsync(cancel).ConfigureAwait(false);
            if (tree is null && seeded)
            {
                // The copy of the repo's index may have been taken while git was writing it. Start
                // from nothing: slower the once, and right.
                TryDelete(IndexFile);
                tree = await AddAndWriteAsync(cancel).ConfigureAwait(false);
            }
            if (tree is not null)
            {
                lock (_treeLock) { _tree = tree; }
            }
            return tree;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> AddAndWriteAsync(CancellationToken cancel)
    {
        var env = Env();
        // --ignore-errors: a file locked by a build must not stop the rest being recorded, though git
        // then exits non-zero. core.fsmonitor off: a watcher daemon has no token for an index it never saw.
        var add = await _git.RunAsync(TopLevel,
            new[] { "-c", "core.fsmonitor=false", "-c", "core.safecrlf=false", "add", "-A", "--ignore-errors", "--", "." },
            env, TimeSpan.FromMinutes(3), cancel: cancel).ConfigureAwait(false);
        if (add.TimedOut)
        {
            _log.Warn("[git] snapshot: git add did not finish in time");
            return null;
        }

        var write = await _git.RunAsync(TopLevel, new[] { "write-tree" }, env, TimeSpan.FromSeconds(60), cancel: cancel).ConfigureAwait(false);
        if (!write.Ok)
        {
            _log.Debug(() => $"[git] snapshot: write-tree failed ({write.ExitCode}): {write.Error.Trim()}");
            return null;
        }
        var tree = write.Output.Trim();
        return tree.Length >= 40 ? tree : null;
    }

    /// <summary>Which paths among <paramref name="relativePaths"/> are ignored by the repo's rules. A
    /// tracked file is never ignored, whatever a pattern says: that is git's own definition.</summary>
    public async Task<HashSet<string>> IgnoredAsync(IReadOnlyCollection<string> relativePaths)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        if (relativePaths.Count == 0) { return ignored; }

        var run = await _git.RunAsync(TopLevel, new[] { "check-ignore", "-z", "--stdin" },
            timeout: TimeSpan.FromSeconds(60), stdin: string.Join("\0", relativePaths) + "\0").ConfigureAwait(false);
        // 0 = some ignored, 1 = none: both are answers. Anything else is git failing.
        if (run.TimedOut || run.ExitCode > 1)
        {
            _log.Debug(() => $"[git] check-ignore failed ({run.ExitCode}): {run.Error.Trim()}");
            return ignored;
        }
        foreach (var path in run.Output.Split('\0').Where(p => p.Length > 0)) { ignored.Add(path); }
        return ignored;
    }

    /// <summary>What <paramref name="tree"/> holds for each of the paths (forward slashes, relative to
    /// the repo's root). A path it does not hold is simply absent from the answer.</summary>
    public async Task<Dictionary<string, TreeBlob>> ListBlobsAsync(string tree, IReadOnlyList<string> relativePaths)
    {
        var found = new Dictionary<string, TreeBlob>(StringComparer.Ordinal);
        foreach (var chunk in Chunks(relativePaths, 6000))
        {
            var args = new List<string> { "ls-tree", "-r", "-z", tree, "--" };
            args.AddRange(chunk);
            var run = await _git.RunAsync(TopLevel, args, Env(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (!run.Ok)
            {
                _log.Debug(() => $"[git] ls-tree failed ({run.ExitCode}): {run.Error.Trim()}");
                continue;
            }
            foreach (var entry in run.Output.Split('\0').Where(e => e.Length > 0))
            {
                // "<mode> <type> <oid>\t<path>"
                var tab = entry.IndexOf('\t');
                if (tab < 0) { continue; }
                var meta = entry.Substring(0, tab).Split(' ');
                if (meta.Length < 3 || meta[1] != "blob") { continue; }
                found[entry.Substring(tab + 1)] = new TreeBlob { Mode = meta[0], Oid = meta[2] };
            }
        }
        return found;
    }

    /// <summary>Write a blob to <paramref name="targetFile"/> as git would check it out: the filters
    /// that turn the stored LF into the file's CRLF are applied, so the bytes are the ones that were on disk.</summary>
    public async Task<bool> WriteBlobAsync(string oid, string relativePath, string targetFile)
    {
        var run = await _git.RunToFileAsync(TopLevel,
            new[] { "cat-file", "--filters", "--path=" + relativePath, oid }, targetFile, Env(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (run.Ok) { return true; }
        _log.Debug(() => $"[git] cat-file {oid} failed ({run.ExitCode}): {run.Error.Trim()}");
        TryDelete(targetFile);
        return false;
    }

    /// <summary>The files that differ between two trees.</summary>
    public async Task<List<string>> ChangedBetweenAsync(string before, string after)
    {
        var run = await _git.RunAsync(TopLevel, new[] { "diff-tree", "-r", "--name-only", "-z", "--no-renames", before, after },
            Env(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!run.Ok) { return new List<string>(); }
        return run.Output.Split('\0').Where(p => p.Length > 0).ToList();
    }

    /// <summary>The path relative to the repo's root, in forward slashes, or null when the file is
    /// outside it or inside a repository of its own (a submodule: <c>add -A</c> records only its
    /// commit, so there is nothing to compare a file in it with).</summary>
    public string Relative(string fullPath)
    {
        var root = TopLevel.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string full;
        try { full = Path.GetFullPath(fullPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { return null; }

        var dir = Path.GetDirectoryName(full);
        while (dir is not null && dir.Length > root.Length - 1)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) { return null; }
            dir = Path.GetDirectoryName(dir);
        }
        return full.Substring(root.Length).Replace('\\', '/');
    }

    private string IndexFile => Path.Combine(_folder, "index");

    private Dictionary<string, string> Env() => new()
    {
        ["GIT_INDEX_FILE"] = IndexFile,
        ["GIT_OBJECT_DIRECTORY"] = Path.Combine(_folder, "objects"),
        ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = _repoObjects,
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>Copy the repo's index the first time, so the stat cache is warm and a file is read
    /// again only if it differs. Whether a copy was made.</summary>
    private bool SeedIndex()
    {
        if (File.Exists(IndexFile) || !File.Exists(_repoIndex)) { return false; }
        try
        {
            File.Copy(_repoIndex, IndexFile, false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(() => $"[git] could not seed the snapshot index: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<List<string>> Chunks(IReadOnlyList<string> paths, int maxChars)
    {
        var chunk = new List<string>();
        var size = 0;
        foreach (var path in paths)
        {
            if (chunk.Count > 0 && size + path.Length + 1 > maxChars)
            {
                yield return chunk;
                chunk = new List<string>();
                size = 0;
            }
            chunk.Add(path);
            size += path.Length + 1;
        }
        if (chunk.Count > 0) { yield return chunk; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover in a folder that is deleted with the pane anyway.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // A snapshot already running holds the gate; its git is killed by the cancellation the owner
        // raised before disposing, so this wait is short. Past the limit the folder is removed anyway
        // and the sweep at the next start takes what remains.
        var held = false;
        try { held = _gate.Wait(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* disposed twice */ }
        try
        {
            ChangeBaselines.DeleteTree(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(() => $"[git] could not remove {_folder}: {ex.Message}");
        }
        finally
        {
            if (held) { _gate.Release(); }
        }
    }
}

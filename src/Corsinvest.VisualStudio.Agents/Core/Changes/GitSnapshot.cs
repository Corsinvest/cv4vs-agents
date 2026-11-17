/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>A tree object that records the working tree at a moment.</summary>
internal sealed class GitTree(string oid, HashSet<string> skipped)
{
    public string Oid { get; } = oid;

    /// <summary>The paths git could not read (a program holds them open), so the tree holds an older version
    /// of each, or none. Null when git said some files were skipped without saying which: such a tree is no
    /// basis for any conclusion about any file.</summary>
    public HashSet<string> Skipped { get; } = skipped;

    /// <summary>Good for comparing, apart from <see cref="Skipped"/>.</summary>
    public bool Usable => Skipped is not null;

    /// <summary>Every file was recorded.</summary>
    public bool Complete => Skipped is { Count: 0 };

    /// <summary>Whether this tree says nothing reliable about <paramref name="relativePath"/>.</summary>
    public bool Skips(string relativePath) => Skipped is null || Skipped.Contains(relativePath);
}
/// <summary>One path that differs between two trees, as <c>git diff-tree --raw</c> reports it.</summary>
internal sealed class TreeChange
{
    /// <summary>Relative to the repository's root, in forward slashes.</summary>
    public string RelativePath { get; set; }

    /// <summary>A (only in the later tree), D (only in the earlier), M, or T (the kind of entry changed).</summary>
    public char Status { get; set; }

    public string OldMode { get; set; }
    public string NewMode { get; set; }
    public string OldOid { get; set; }
    public string NewOid { get; set; }

    /// <summary>A plain file. A symlink (120000) or a submodule (160000) is not something to restore by
    /// writing bytes.</summary>
    public bool OldIsFile => IsFile(OldMode);

    public bool NewIsFile => IsFile(NewMode);

    /// <summary>A repository nested in this one shows up as a single entry, its commit: what changes inside
    /// it is its own repository's business.</summary>
    public bool IsSubmodule => OldMode == "160000" || NewMode == "160000";

    private static bool IsFile(string mode) => mode == "100644" || mode == "100755";
}

/// <summary>A picture of a working tree at a moment, kept as a git tree object: the "everything goes
/// into a snapshot, then compared" that tells what a shell command changed and what a file was before it.
/// <para>Taken with a private index and a private object store, so nothing is written into the user's
/// repository: <c>git add -A</c> runs against a copy of the repo's index (seeded once, so the stat cache
/// is warm and only files that differ are read) and writes its new objects into this folder, with the
/// repo's own object store as an alternate, so the blobs git already has are reused rather than stored twice.
/// A repository that uses Git LFS is the exception: its clean filter keeps its large files in its own
/// folder under <c>.git</c>, whichever object store is asked.</para>
/// <para>Two snapshots are compared as trees (<see cref="DiffAsync"/>), never path by path: the same
/// <c>add -A</c> built both, so they agree on what is ignored, on the case of a name and on what a file
/// is, which a lookup of paths seen by a file watcher would not.</para>
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
    private GitTree _tree;
    private volatile bool _disposed;

    // Whether the last git add ran out of time. Written and read under the gate.
    private bool _timedOut;

    // A picture asked for while another is being taken would be taken after it, and so would every other
    // one asked for meanwhile: a burst of commands against a slow repository would queue a full scan each.
    // Every request takes a number; a run serves every request numbered before it began, and the others
    // that waited behind it take its result. All under the gate, but the counter.
    private FileStream _owner;
    private long _requests;
    private long _lastRunServed;
    private GitTree _lastResult;
    private int _runs;

    /// <summary>How many pictures were really taken, as against asked for.</summary>
    internal int Runs => Volatile.Read(ref _runs);

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

    /// <summary>The newest <see cref="GitTree.Usable"/> tree taken, or null before the first one. It may
    /// <see cref="GitTree.Skipped">skip</see> some paths, and whoever compares with it has to ask
    /// <see cref="GitTree.Skips"/> about each; one that is not usable at all is never remembered.</summary>
    public GitTree LatestTree { get { lock (_treeLock) { return _tree; } } }

    /// <summary>Find the repository <paramref name="workingDirectory"/> is in. Null when it is in none:
    /// without one there is nothing to take a snapshot of.</summary>
    /// <param name="failed">Told what git said when it would not open the folder (empty when it did not
    /// answer at all), so that "not a repository" can be told from "refused" and "too slow".</param>
    public static async Task<GitSnapshot> OpenAsync(GitRunner git, string workingDirectory, string folder, OutputWindowLogger log,
                                                    Action<string> failed = null)
    {
        var found = await git.RunAsync(workingDirectory,
            new[] { "rev-parse", "--show-toplevel", "--git-path", "objects", "--git-path", "index" },
            timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!found.Ok)
        {
            failed?.Invoke(found.TimedOut ? "" : found.Error ?? "");
            return null;
        }

        var lines = found.Output.Split('\n').Select(l => l.Trim('\r')).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 3)
        {
            failed?.Invoke("git answered with something unexpected: " + FirstLine(found.Output));
            return null;
        }

        // Relative to where git ran, and in forward slashes: both made plain here.
        string Absolute(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
        return new GitSnapshot(git, Absolute(lines[0]), Absolute(lines[1]), Absolute(lines[2]), folder, log);
    }

    /// <summary>A repository rooted at a drive or at the user's profile folder (dotfiles set up that way
    /// do it, and a solution folder that is not a repository then resolves to it). Everything under it
    /// that is not ignored counts as an untracked file, so a snapshot would copy the whole profile or disk
    /// into the store. Not worth following. <paramref name="home"/> is a parameter for the tests.</summary>
    public static bool IsTooBroad(string topLevel, string home = null)
    {
        if (string.IsNullOrEmpty(topLevel)) { return false; }
        string Plain(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var top = Plain(topLevel);
        var root = Path.GetPathRoot(Path.GetFullPath(topLevel))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(top, root, StringComparison.OrdinalIgnoreCase)) { return true; }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && string.Equals(top, Plain(home), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The full path of a repository-relative one (as <see cref="TreeChange.RelativePath"/> has it).</summary>
    public string Absolute(string relativePath)
        => Path.GetFullPath(Path.Combine(TopLevel, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Take a snapshot of the working tree as it is now. Null when git could not do it (logged); a
    /// tree with some <see cref="GitTree.Skipped"/> paths when it could do it only in part. One at a time:
    /// two runs over the same index would trip over each other.</summary>
    public async Task<GitTree> TakeAsync(CancellationToken cancel = default)
    {
        var request = Interlocked.Increment(ref _requests);
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            // A picture requested just before the chat closed would otherwise remake the folder that
            // closing it removed.
            if (_disposed) { return null; }
            // A run that began after this was asked for is as good as one made for it.
            if (_lastRunServed >= request) { return _lastResult; }
            _lastRunServed = Interlocked.Read(ref _requests);
            // Nothing until this run has a result: if it throws, those waiting behind it must not be handed
            // the previous run's picture as if it were theirs.
            _lastResult = null;
            Interlocked.Increment(ref _runs);

            Directory.CreateDirectory(Path.Combine(_folder, "objects"));
            HoldOwnerMarker();
            await SeedIndexAsync().ConfigureAwait(false);
            // A git that was killed (a timeout, the chat closing) leaves its lock behind. This folder is ours
            // alone and the gate is held, so no other git can be holding it: a stale lock would otherwise
            // fail every picture from here on.
            TryDelete(IndexLock);
            _timedOut = false;
            var tree = await AddAndWriteAsync(cancel).ConfigureAwait(false);
            if (tree is null && !_timedOut && !cancel.IsCancellationRequested)
            {
                // The index may be a copy taken while git was writing the repo's own, or hurt by a run that
                // was killed, or name objects the repo has since dropped. Start from nothing: slower the
                // once, and right. Not after a timeout: it would only time out again.
                TryDelete(IndexFile);
                TryDelete(IndexLock);
                tree = await AddAndWriteAsync(cancel).ConfigureAwait(false);
            }
            if (tree is { Usable: true })
            {
                lock (_treeLock) { _tree = tree; }
            }
            _lastResult = tree;
            return tree;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GitTree> AddAndWriteAsync(CancellationToken cancel)
    {
        var env = Env();
        // --ignore-errors: a file locked by a build must not stop the rest being recorded, though git
        // then exits non-zero. core.fsmonitor off: a watcher daemon has no token for an index it never saw.
        var add = await _git.RunAsync(TopLevel,
            new[] { "-c", "core.fsmonitor=false", "-c", "core.safecrlf=false", "add", "-A", "--ignore-errors", "--", "." },
            env, TimeSpan.FromMinutes(3), cancel: cancel).ConfigureAwait(false);
        if (cancel.IsCancellationRequested) { return null; }
        if (add.TimedOut)
        {
            _timedOut = true;
            _log.Warn("[git] snapshot: git add did not finish in time");
            return null;
        }

        // Exit 0: everything was recorded. Exit 1 with "error:" lines and no "fatal:": --ignore-errors
        // skipped some files and recorded the rest. Anything else (a lock, a corrupt index) recorded nothing,
        // and a tree written from what the index held before would be a stale picture passed off as a new one.
        HashSet<string> skipped;
        if (add.ExitCode == 0)
        {
            skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else if (add.ExitCode == 1 && !add.Error.Contains("fatal:"))
        {
            // Which files, if git said so in words this knows. If not, the tree is kept out of every
            // comparison: guessing which paths to trust is how a file that exists gets called new.
            TrySkippedPaths(add.Error, out skipped);
            _log.Debug(() => skipped is null
                ? $"[git] snapshot: some files could not be read, and git did not say which: {FirstLine(add.Error)}"
                : $"[git] snapshot: {skipped.Count} file(s) could not be read: {string.Join(", ", skipped)}");
        }
        else
        {
            _log.Debug(() => $"[git] snapshot: git add failed ({add.ExitCode}): {FirstLine(add.Error)}");
            return null;
        }

        var write = await _git.RunAsync(TopLevel, new[] { "write-tree" }, env, TimeSpan.FromSeconds(60), cancel: cancel).ConfigureAwait(false);
        if (!write.Ok)
        {
            if (!cancel.IsCancellationRequested) { _log.Debug(() => $"[git] snapshot: write-tree failed ({write.ExitCode}): {FirstLine(write.Error)}"); }
            return null;
        }
        var oid = write.Output.Trim();
        return oid.Length >= 40 ? new GitTree(oid, skipped) : null;
    }

    private static readonly Regex OpenError = new(@"^error: open\(""(?<p>.*)""\): .*$", RegexOptions.Compiled);
    private static readonly Regex IndexError = new(@"^error: unable to index file '?(?<p>.*?)'?$", RegexOptions.Compiled);

    /// <summary>The files <c>git add --ignore-errors</c> skipped, from what it printed: an "open" error and an
    /// "unable to index" error per file. Every error line has to be one of those two shapes: one that is
    /// neither (a read error halfway through a file, a format of a git newer than this) means the list cannot
    /// be trusted to be all of them, and <paramref name="skipped"/> is null. Warnings and hints are advice,
    /// not skipped files (a repository nested in this one brings a few on every add), and are not read.</summary>
    internal static bool TrySkippedPaths(string stderr, out HashSet<string> skipped)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (stderr ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("warning:", StringComparison.Ordinal) || line.StartsWith("hint:", StringComparison.Ordinal)) { continue; }

            var match = OpenError.Match(line);
            if (!match.Success) { match = IndexError.Match(line); }
            if (!match.Success)
            {
                skipped = null;
                return false;
            }
            paths.Add(match.Groups["p"].Value);
        }

        skipped = paths.Count > 0 ? paths : null;
        return skipped is not null;
    }

    /// <summary>The paths that differ between two trees. Null when git could not say (logged): not the
    /// same as an empty list, which is the answer "nothing changed".</summary>
    public async Task<List<TreeChange>> DiffAsync(string beforeTree, string afterTree)
    {
        var run = await _git.RunAsync(TopLevel,
            new[] { "diff-tree", "-r", "--raw", "-z", "--no-renames", "--no-abbrev", beforeTree, afterTree },
            Env(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (!run.Ok)
        {
            _log.Warn($"[git] could not compare two snapshots ({run.ExitCode}): {FirstLine(run.Error)}");
            return null;
        }
        return ParseRaw(run.Output);
    }

    /// <summary>Read <c>diff-tree --raw -z</c>: <c>:oldmode newmode oldoid newoid status NUL path NUL</c>,
    /// repeated.</summary>
    internal static List<TreeChange> ParseRaw(string output)
    {
        var changes = new List<TreeChange>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var header = parts[i];
            if (header.Length == 0 || header[0] != ':' || i + 1 >= parts.Length) { continue; }

            var meta = header.Substring(1).Split(' ');
            if (meta.Length < 5 || meta[4].Length == 0) { continue; }
            changes.Add(new TreeChange
            {
                OldMode = meta[0],
                NewMode = meta[1],
                OldOid = meta[2],
                NewOid = meta[3],
                Status = meta[4][0],
                RelativePath = parts[i + 1],
            });
            i++;
        }
        return changes;
    }

    /// <summary>Write a blob to <paramref name="targetFile"/> as git would check it out: the filters
    /// that turn the stored LF into the file's CRLF are applied, so the bytes are the ones that were on disk.</summary>
    public async Task<bool> WriteBlobAsync(string oid, string relativePath, string targetFile)
    {
        var run = await _git.RunToFileAsync(TopLevel,
            new[] { "cat-file", "--filters", "--path=" + relativePath, oid }, targetFile, Env(), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (run.Ok) { return true; }
        _log.Debug(() => $"[git] cat-file {oid} failed ({run.ExitCode}): {FirstLine(run.Error)}");
        TryDelete(targetFile);
        return false;
    }

    /// <summary>The file a store holds open while it lives, so that a Visual Studio starting up can tell a
    /// store that is in use (however long it has been idle) from one left behind by a crash: the sweep
    /// leaves alone a folder whose marker it cannot open.</summary>
    internal const string OwnerMarker = ".owner";

    private void HoldOwnerMarker()
    {
        if (_owner is not null) { return; }
        try
        {
            _owner = new FileStream(Path.Combine(_folder, OwnerMarker), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without it the store is judged by its age, as before.
            _log.Debug(() => $"[git] could not mark {_folder} as in use: {ex.Message}");
        }
    }

    private string IndexFile => Path.Combine(_folder, "index");

    private string IndexLock => IndexFile + ".lock";

    private Dictionary<string, string> Env() => new()
    {
        ["GIT_INDEX_FILE"] = IndexFile,
        ["GIT_OBJECT_DIRECTORY"] = Path.Combine(_folder, "objects"),
        ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = _repoObjects,
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    /// <summary>Copy the repo's index the first time, so the stat cache is warm and a file is read
    /// again only if it differs. Whether a copy was made.</summary>
    private async Task<bool> SeedIndexAsync()
    {
        if (File.Exists(IndexFile) || !File.Exists(_repoIndex)) { return false; }
        try
        {
            await AsyncFile.CopyAsync(_repoIndex, IndexFile).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Debug(() => $"[git] could not seed the snapshot index: {ex.Message}");
            return false;
        }
    }

    private static string FirstLine(string text)
        => (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

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
        // Closed first: a folder cannot be deleted under a file held open in it.
        try { _owner?.Dispose(); }
        catch (IOException) { /* gone with the process anyway */ }
        _owner = null;
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

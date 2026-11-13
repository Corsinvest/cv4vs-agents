/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The copies this extension takes for itself, one folder per session under
/// <paramref name="root"/> (<see cref="AppPaths.BaselinesFolder"/> outside tests).
/// <para>Only where the CLI's own backups do not reach: the CLI writes none for a sub-agent's edit,
/// and none at the moment a file is kept, so the next edit after a Keep has to be compared with a
/// copy taken then. Everything else is read from the CLI's <c>file-history</c>, which is the point of
/// not capturing twice.</para></summary>
internal sealed class ChangeBaselines(string root, OutputWindowLogger log)
{
    /// <summary>The folder the running extension uses.</summary>
    public static ChangeBaselines Default { get; } = new(AppPaths.BaselinesFolder, OutputWindowLogger.Global);

    private string _rootPrefix;

    /// <summary>Copy <paramref name="path"/> as it is now. <see cref="BaselineRef.Empty"/> when it does
    /// not exist (the baseline is "nothing"), <see cref="BaselineRef.None"/> when it could not be read.</summary>
    public async Task<BaselineRef> CaptureAsync(string sessionId, string path, CancellationToken cancel = default)
    {
        string target = null;
        try
        {
            // Whether the file exists and the folder have no async form, so they go to the pool; the content is copied through
            // an asynchronous stream.
            target = await Task.Run(() => File.Exists(path) ? TargetFor(sessionId, path) : null, cancel).ConfigureAwait(false);
            if (target is null) { return BaselineRef.Empty; }
            await AsyncFile.CopyAsync(path, target, cancel).ConfigureAwait(false);
            return BaselineRef.Own(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not keep a copy of {path}: {ex.Message}");
            // What was written before the failure is not a copy of anything.
            DeleteQuietly(target);
            return BaselineRef.None;
        }
    }

    private static void DeleteQuietly(string path)
    {
        if (path is null) { return; }
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private int _sequence;

    /// <summary>Where a copy of <paramref name="path"/> belongs, with its folder made. The hash keeps two
    /// files of the same name apart; the name after it keeps the extension, so the copy opens with the
    /// editor the original would. Every copy has a name of its own, even of the same file: a command and
    /// an edit can be in flight on it together, as can two sub-agents, and one must not overwrite, or
    /// delete, the copy the other is going to compare with. For a caller that writes the copy itself
    /// (git, for a blob).</summary>
    public string TargetFor(string sessionId, string path)
    {
        var folder = FolderFor(sessionId);
        Directory.CreateDirectory(folder);
        var copy = Interlocked.Increment(ref _sequence).ToString("x");
        return Path.Combine(folder, $"{ShortHash(path)}.{copy}~{Path.GetFileName(path)}");
    }

    /// <summary>Delete a copy this extension took. A CLI backup, or anything outside the baselines
    /// folder, is left alone whatever the caller believes it owns. There is no async delete.</summary>
    public void Release(BaselineRef baseline)
    {
        if (!baseline.Owned || string.IsNullOrEmpty(baseline.Path) || !IsInside(baseline.Path)) { return; }
        try { File.Delete(baseline.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not remove {baseline.Path}: {ex.Message}");
        }
    }

    /// <summary>Remove the folders of sessions nobody has touched for <paramref name="olderThan"/>:
    /// a pane that was killed with VS never got to clean up after itself.</summary>
    public void Sweep(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(root)) { return; }
            var limit = DateTime.UtcNow - olderThan;
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (NewestActivity(folder) >= limit || IsOwned(folder)) { continue; }
                try { DeleteTree(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"[changes] could not sweep {folder}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not sweep {root}: {ex.Message}");
        }
    }

    /// <summary>The newest time anything in the folder was made or changed. The folder's own time moves only
    /// when an entry is added or removed, and a copy keeps the write time of the file it came from, so the
    /// time a copy was made counts too: otherwise a chat left open for weeks, with copies that were never
    /// added to or removed, looks abandoned to the next Visual Studio that starts.</summary>
    private static DateTime NewestActivity(string folder)
    {
        var newest = Latest(Directory.GetCreationTimeUtc(folder), Directory.GetLastWriteTimeUtc(folder));
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            newest = Latest(newest, Latest(File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file)));
        }
        return newest;
    }

    private static DateTime Latest(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>The folder belongs to a process that is running: its owner marker is held open. However long
    /// the store has been idle it is not an orphan, and deleting it would take the pictures a chat still
    /// compares with. A folder with no marker (baselines have none) is judged by its age alone.</summary>
    private static bool IsOwned(string folder)
    {
        var marker = Path.Combine(folder, GitSnapshot.OwnerMarker);
        if (!File.Exists(marker)) { return false; }
        try
        {
            // Nobody else may have it open for this to work, so it fails exactly when somebody has.
            using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None)) { return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public string FolderFor(string sessionId)
        => Path.Combine(root, SessionManager.IsSafePathToken(sessionId) ? sessionId : "unsaved");

    private bool IsInside(string path)
    {
        try
        {
            _rootPrefix ??= Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Delete a folder and everything in it, read-only files included. Git writes its objects
    /// read-only, and a plain recursive delete stops at the first of them, leaving the store behind.</summary>
    internal static void DeleteTree(string folder)
    {
        if (!Directory.Exists(folder)) { return; }
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) { File.SetAttributes(file, FileAttributes.Normal); }
        }
        Directory.Delete(folder, true);
    }

    // FNV-1a over the lower-cased path. Not a security hash: it only has to keep names apart, and
    // the framework's own hashes throw on a machine with FIPS mode on.
    internal static string ShortHash(string path)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in path.ToLowerInvariant())
            {
                hash = (hash ^ c) * 16777619u;
            }
            return hash.ToString("x8");
        }
    }
}
